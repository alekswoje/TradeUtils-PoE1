using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TradeUtils.Utility;

namespace TradeUtils;

public partial class TradeUtils
{
    // Honest, identifying User-Agent for the read path (search / fetch / live websocket).
    // GGG's API docs ask third-party tools to identify themselves rather than impersonate a
    // browser, and this is what maintained tools (PoB, 5k-mirrors) do. The whisper/teleport
    // action path is intentionally left as-is — see README "Account risk".
    public const string PluginUserAgent = "TradeUtils/1.0 (+https://github.com/alekswoje/TradeUtils-PoE1)";

    // Current challenge league from GGG's trade API, fetched once and cached. Only used as a
    // fallback when the in-game league can't be read (e.g. at the login/character screen).
    private volatile string _apiLeague;
    private int _apiLeagueFetching; // 0 = idle, 1 = a fetch is in flight

    // Every pc league the trade API knows about, from the same unauthenticated call. Not a source
    // of truth for which league you're IN — that's the whole problem — but it's exactly the list of
    // valid things to type into League Override, which saves guessing at the spelling.
    private volatile string[] _apiLeagues;

    // The league of the character actually being played, from GGG's character list. This is the
    // only authoritative answer available: ServerData.League reads empty even while fully in-world,
    // and the trade API's first entry is the current CHALLENGE league — which is simply wrong when
    // you're playing Standard, Hardcore, or anything else.
    private volatile string _characterLeague;
    private volatile string _characterLeagueFor; // character name the above was resolved for
    private int _characterLeagueFetching;
    private DateTime _characterLeagueNextAttempt = DateTime.MinValue;

    private const string CharactersApiUrl = "https://www.pathofexile.com/character-window/get-characters";

    /// <summary>Name of the character currently being played, or null outside the game.</summary>
    internal string CurrentCharacterName()
    {
        try
        {
            var name = GameController?.Player?.GetComponent<ExileCore.PoEMemory.Components.Player>()?.PlayerName;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the league to use for trade requests, in priority order:
    ///   1. The league the player is currently in (read from game memory).
    ///   2. The current challenge league from GGG's trade API (fetched once, cached).
    ///   3. "Standard" as a last resort.
    /// This replaces the old hardcoded "Keepers"/"Standard" values, which broke on every
    /// league rollover and produced the API's generic "Invalid query" (HTTP 400) error.
    /// </summary>
    internal string ResolveLeague()
    {
        var known = ResolveLeagueOrNull();
        if (!string.IsNullOrWhiteSpace(known)) return known;

        // Searches can live with a guess — a query aimed at the wrong league just fails, and
        // guessing the current challenge league is right for most people most of the time. Pricing
        // cannot, which is why it uses ResolveLeagueOrNull and refuses on null.
        var cached = _apiLeague;
        if (!string.IsNullOrWhiteSpace(cached)) return cached;

        _ = EnsureApiLeagueAsync();
        return "Standard";
    }

    /// <summary>
    /// Same resolution as <see cref="ResolveLeague"/>, but returns null instead of guessing
    /// "Standard" when the league genuinely isn't known yet.
    ///
    /// The difference matters. A read-only query sent to the wrong league just fails, but anything
    /// that *prices* against the wrong league is silently wrong: poe.ninja happily answers for
    /// Standard, where a Divine is ~829c instead of ~174c, and repricing off that number relists
    /// real items at roughly five times their intended price. Callers that write must use this and
    /// refuse to act on null.
    /// </summary>
    internal string ResolveLeagueOrNull()
    {
        // An explicit override counts as authoritative - the user knows which league they are in,
        // and this is the only path that doesn't depend on an API call succeeding.
        try
        {
            var manual = Settings?.LeagueOverride?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(manual))
            {
                if (manual.StartsWith("SSF ", StringComparison.OrdinalIgnoreCase))
                    manual = manual.Substring(4).Trim();
                if (!string.IsNullOrWhiteSpace(manual)) return manual;
            }
        }
        catch
        {
            // Settings not constructed yet during load.
        }

        try
        {
            var live = GameController?.IngameState?.ServerData?.League;
            if (!string.IsNullOrWhiteSpace(live))
            {
                live = live.Trim();
                // SSF leagues aren't tradeable; map to the parent so a stray call still targets a real league.
                if (live.StartsWith("SSF ", StringComparison.OrdinalIgnoreCase))
                    live = live.Substring(4).Trim();
                if (!string.IsNullOrWhiteSpace(live))
                    return live;
            }
        }
        catch
        {
            // ServerData can throw during load screens / area transitions — fall through.
        }

        // Ask GGG which league this character is in. Deliberately NOT falling back to _apiLeague
        // here: that is the current challenge league, which is a guess, and a guessed league prices
        // Standard items against challenge-league rates (a Divine is ~829c against ~174c). Callers
        // that write must get a real answer or nothing.
        var character = CurrentCharacterName();
        if (character == null) return null;

        if (string.Equals(character, _characterLeagueFor, StringComparison.Ordinal))
        {
            var known = _characterLeague;
            if (!string.IsNullOrWhiteSpace(known)) return known;
        }

        _ = EnsureCharacterLeagueAsync(character);
        return null;
    }

    /// <summary>
    /// Why <see cref="ResolveLeagueOrNull"/> is coming back null, phrased as something to do about
    /// it. Repricing refuses without an authoritative league, and the log messages explaining that
    /// never reach the log files — so this is what gets shown on the value display instead.
    /// </summary>
    internal string LeagueUnresolvedReason()
    {
        if (CurrentCharacterName() == null)
            return "no character is loaded yet";

        // Setting the override is always the fix and never needs a session — it just isn't
        // automatic. The auto path needs one because the client doesn't put the league anywhere
        // readable (ServerData.League is empty even in-world, verified), so the only thing that can
        // say which league a character is in is GGG's own character list, which is authenticated.
        return "set League Override" +
               (string.IsNullOrWhiteSpace(EncryptedSettings.GetSecureSessionId())
                   ? " (or add a POESESSID to auto-detect)"
                   : " — GGG's character list hasn't answered");
    }

    /// <summary>
    /// The league names League Override will accept, or null before the list has been fetched.
    /// Kept apart from <see cref="LeagueUnresolvedReason"/> because that one has to stay short
    /// enough to sit inline in a one-line run summary.
    /// </summary>
    internal string KnownLeagueNames()
    {
        var known = _apiLeagues;
        return known == null || known.Length == 0 ? null : string.Join(", ", known);
    }

    /// <summary>
    /// Looks up the league of <paramref name="character"/> in GGG's character list and caches it.
    /// Runs at most once at a time, and re-runs when the character changes.
    /// </summary>
    internal async Task EnsureCharacterLeagueAsync(string character)
    {
        if (string.IsNullOrWhiteSpace(character)) return;
        if (string.Equals(character, _characterLeagueFor, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(_characterLeague))
            return;

        // ResolveLeagueOrNull runs from the render loop, so without a backoff every failure here
        // fires another request on the very next frame - a request storm aimed at GGG.
        if (DateTime.Now < _characterLeagueNextAttempt) return;
        if (Interlocked.Exchange(ref _characterLeagueFetching, 1) == 1) return;
        _characterLeagueNextAttempt = DateTime.Now.AddMinutes(2);

        try
        {
            var sessionId = EncryptedSettings.GetSecureSessionId();
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                LogMessage("League: POESESSID isn't set, so the character's league can't be confirmed. " +
                           "Repricing stays disabled until it is — set it in settings.");
                return;
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, CharactersApiUrl))
            {
                request.Headers.Add("User-Agent", PluginUserAgent);
                request.Headers.Add("Cookie", $"POESESSID={sessionId}");

                using (var response = await _httpClient.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        LogError($"League: character list returned HTTP {(int)response.StatusCode}. " +
                                 "A 401 means the POESESSID has expired.");
                        return;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    var characters = JArray.Parse(json);

                    foreach (var entry in characters)
                    {
                        var name = (string)entry["name"];
                        if (!string.Equals(name, character, StringComparison.Ordinal)) continue;

                        var league = ((string)entry["league"])?.Trim();
                        if (string.IsNullOrWhiteSpace(league)) break;

                        // SSF isn't tradeable; price against the parent league it mirrors.
                        if (league.StartsWith("SSF ", StringComparison.OrdinalIgnoreCase))
                            league = league.Substring(4).Trim();

                        _characterLeague = league;
                        _characterLeagueFor = character;
                        _characterLeagueNextAttempt = DateTime.MinValue;
                        LogMessage($"League: '{character}' is in '{league}'.");
                        return;
                    }

                    LogError($"League: '{character}' wasn't in the account's character list, so its league " +
                             "is unknown. Repricing stays disabled.");
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"League: couldn't look up the character's league ({ex.Message}).");
        }
        finally
        {
            Interlocked.Exchange(ref _characterLeagueFetching, 0);
        }
    }

    /// <summary>
    /// Applies league auto-detection. When "Auto-Detect League" is on (default) or no league has
    /// been configured, use the live/resolved league; otherwise honour the user's explicit override.
    /// </summary>
    internal string EffectiveLeague(string configured)
    {
        if (Settings.AutoDetectLeague.Value || string.IsNullOrWhiteSpace(configured))
            return ResolveLeague();
        return configured.Trim();
    }

    /// <summary>Fetches and caches the current challenge league from GGG's trade API. Runs at most once at a time.</summary>
    internal async Task EnsureApiLeagueAsync()
    {
        if (!string.IsNullOrWhiteSpace(_apiLeague))
            return;
        // Only one in-flight fetch at a time. If another is running, let it finish.
        if (Interlocked.Exchange(ref _apiLeagueFetching, 1) == 1)
            return;

        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://www.pathofexile.com/api/trade/data/leagues"))
            {
                request.Headers.Add("User-Agent", PluginUserAgent);
                request.Headers.Add("Accept", "*/*");

                using (var response = await _httpClient.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        LogError($"League auto-detect: GGG /api/trade/data/leagues returned HTTP {(int)response.StatusCode}. Using in-game league or 'Standard'.");
                        return;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    var result = JObject.Parse(json)["result"] as JArray;
                    if (result == null)
                        return;

                    // The first pc-realm entry is the current main (softcore) challenge league. The
                    // rest are kept only to show the user what League Override will accept.
                    var names = new System.Collections.Generic.List<string>();
                    foreach (var entry in result)
                    {
                        var realm = (string)entry["realm"];
                        if (realm != null && realm != "pc")
                            continue;

                        var id = (string)entry["id"];
                        if (!string.IsNullOrWhiteSpace(id))
                            names.Add(id.Trim());
                    }

                    if (names.Count > 0)
                    {
                        _apiLeagues = names.ToArray();
                        _apiLeague = names[0];
                        LogMessage($"League auto-detect: current league resolved to '{_apiLeague}' from GGG trade API.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"League auto-detect failed: {ex.Message}. Using in-game league or 'Standard'.");
        }
        finally
        {
            Interlocked.Exchange(ref _apiLeagueFetching, 0);
        }
    }
}
