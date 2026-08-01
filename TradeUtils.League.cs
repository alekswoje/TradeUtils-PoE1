using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

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
        return ResolveLeagueOrNull() ?? "Standard";
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
            // ServerData can throw during load screens / area transitions — fall through to the cached API value.
        }

        var cached = _apiLeague;
        if (!string.IsNullOrWhiteSpace(cached))
            return cached;

        // Nothing cached yet: kick off a one-time background fetch so a later call resolves it.
        // In practice this is the path that runs, because ServerData.League reads empty even while
        // fully in-world, so the API is what actually resolves the league.
        _ = EnsureApiLeagueAsync();
        return null;
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

                    // The first pc-realm entry is the current main (softcore) challenge league.
                    foreach (var entry in result)
                    {
                        var realm = (string)entry["realm"];
                        if (realm != null && realm != "pc")
                            continue;

                        var id = (string)entry["id"];
                        if (!string.IsNullOrWhiteSpace(id))
                        {
                            _apiLeague = id.Trim();
                            LogMessage($"League auto-detect: current league resolved to '{_apiLeague}' from GGG trade API.");
                            return;
                        }
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
