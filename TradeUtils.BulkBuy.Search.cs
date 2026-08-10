using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TradeUtils.Models;

namespace TradeUtils;

/// <summary>
/// Turns a pasted trade search link into a concrete, ordered list of listings to buy.
///
/// The old BulkBuy asked people to paste the raw JSON POST body out of their browser's network tab,
/// while the in-plugin help told them to "paste trade search URLs". Anyone who followed the help got
/// an HTTP 400 "Invalid query" and concluded the feature was broken, which is more or less what
/// happened.
/// </summary>
public partial class TradeUtils
{
    /// <summary>Trade result ids can only be fetched 10 at a time.</summary>
    private const int TradeFetchBatchSize = 10;

    /// <summary>
    /// Matches the search id and league out of a trade URL. Handles both the plain
    /// /trade/search/{league}/{id} form and the realm-qualified /trade/search/{realm}/{league}/{id}
    /// form that xbox/sony links use.
    /// </summary>
    private static readonly Regex TradeUrlRegex = new Regex(
        @"pathofexile\.com/trade/search/(?:(?<realm>pc|xbox|sony)/)?(?<league>[^/?#]+)/(?<id>[^/?#]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A search id on its own: the trailing segment of a trade URL.</summary>
    private static readonly Regex BareSearchIdRegex = new Regex(
        @"^[A-Za-z0-9]{6,}$", RegexOptions.Compiled);

    /// <summary>
    /// One listing the run intends to buy, flattened out of the search/fetch responses so the buy
    /// loop can walk a plain ordered list instead of re-deriving structure from nested batches.
    /// </summary>
    private sealed class PendingPurchase
    {
        public ResultItem Listing;
        public string SearchName;
        public string League;
        public string SellerAccount;
        public string DisplayName;
        public string PriceText;
        public int StashX;
        public int StashY;
        public string HideoutToken;
        public string ItemId;

        /// <summary>
        /// When the hideout token stops working. It is a JWT with an <c>exp</c> claim, and the whole
        /// queue's tokens are minted at once — so on a long run the later entries are using tokens
        /// issued half an hour earlier. An expired token fails the same way a sold listing does.
        /// </summary>
        public DateTime TokenExpiresAt;

        public bool TokenExpiringWithin(TimeSpan margin)
        {
            // An unparseable expiry is treated as expiring: better one wasted refresh than a
            // teleport that silently fails and gets recorded as a sale.
            if (TokenExpiresAt == DateTime.MinValue) return true;
            return DateTime.Now + margin >= TokenExpiresAt;
        }
    }

    /// <summary>
    /// Pulls the league and search id out of whatever the user pasted. Accepts a full trade URL, a
    /// URL without a scheme, or a bare search id (in which case the league is left null for the
    /// caller to resolve).
    /// </summary>
    /// <returns>true if a search id was recovered.</returns>
    internal static bool TryParseTradeUrl(string input, out string league, out string searchId)
    {
        league = null;
        searchId = null;

        if (string.IsNullOrWhiteSpace(input)) return false;
        input = input.Trim();

        var match = TradeUrlRegex.Match(input);
        if (match.Success)
        {
            // Leagues carry spaces ("Hardcore Allflame") and arrive percent-encoded in a URL.
            league = Uri.UnescapeDataString(match.Groups["league"].Value).Trim();
            searchId = match.Groups["id"].Value.Trim();
            return !string.IsNullOrWhiteSpace(searchId);
        }

        if (BareSearchIdRegex.IsMatch(input))
        {
            searchId = input;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Recovers the stored query behind a search id via GET /api/trade/search/{league}/{id}.
    ///
    /// The query is returned raw and re-posted verbatim. Round-tripping it through typed models
    /// would quietly drop filters the plugin doesn't know about, and a dropped filter here means
    /// buying items the user never asked for — the identified/unidentified split being the obvious
    /// way to lose a lot of currency at once.
    /// </summary>
    private async Task<JObject> ResolveSavedSearchQueryAsync(
        string league,
        string searchId,
        string sessionId,
        CancellationToken ct)
    {
        // This counts against the same budget as a search. Leaving it unmetered doubled the request
        // rate at run start and was part of why later searches came back rate-limited.
        await WaitForSearchQuotaAsync("saved search lookup", ct);

        string url = $"https://www.pathofexile.com/api/trade/search/{Uri.EscapeDataString(league)}/{Uri.EscapeDataString(searchId)}";

        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            ApplyTradeHeaders(request, league, sessionId);

            using (var response = await _httpClient.SendAsync(request, ct))
            {
                if (_rateLimiter != null)
                    await _rateLimiter.HandleRateLimitResponse(response);

                string body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    // 404 here is nearly always the league being wrong rather than the id being
                    // wrong: the same id under the wrong league simply doesn't exist.
                    LogError($"BulkBuy: Could not load search '{searchId}' in league '{league}' — {(int)response.StatusCode} {response.StatusCode}: {Truncate(body, 300)}");
                    return null;
                }

                SavedSearchResponse saved;
                try
                {
                    saved = JsonConvert.DeserializeObject<SavedSearchResponse>(body);
                }
                catch (Exception ex)
                {
                    LogError($"BulkBuy: Could not parse the saved search '{searchId}' — {ex.Message}");
                    return null;
                }

                if (saved?.Query == null)
                {
                    LogError($"BulkBuy: Search '{searchId}' came back without a query. Body: {Truncate(body, 300)}");
                    return null;
                }

                return saved.Query;
            }
        }
    }

    /// <summary>
    /// Runs a query and returns the ordered result ids. Sorting is forced to cheapest-first unless
    /// the saved search already carries its own sort, so a run that stops early stops having bought
    /// the cheapest matches rather than an arbitrary slice.
    /// </summary>
    private async Task<TradeSearchResponse> RunTradeSearchAsync(
        string league,
        JObject query,
        JToken sort,
        string sessionId,
        CancellationToken ct)
    {
        await WaitForSearchQuotaAsync("search", ct);

        var payload = new JObject
        {
            ["query"] = query,
            ["sort"] = sort ?? new JObject { ["price"] = "asc" }
        };

        string url = $"https://www.pathofexile.com/api/trade/search/{Uri.EscapeDataString(league)}";

        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            ApplyTradeHeaders(request, league, sessionId);
            request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

            using (var response = await _httpClient.SendAsync(request, ct))
            {
                if (_rateLimiter != null)
                    await _rateLimiter.HandleRateLimitResponse(response);

                string body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    LogError($"BulkBuy: Search failed in league '{league}' — {(int)response.StatusCode} {response.StatusCode}: {Truncate(body, 300)}");
                    return null;
                }

                try
                {
                    return JsonConvert.DeserializeObject<TradeSearchResponse>(body);
                }
                catch (Exception ex)
                {
                    LogError($"BulkBuy: Could not parse the search response — {ex.Message}");
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// Fetches full listing detail for result ids, 10 at a time, preserving the search's order.
    /// Ids that come back empty are dropped: that means the listing sold between the search and the
    /// fetch, which is normal rather than an error.
    /// </summary>
    private async Task<List<ResultItem>> FetchListingsAsync(
        string league,
        IReadOnlyList<string> resultIds,
        string queryId,
        string sessionId,
        int maxWanted,
        CancellationToken ct)
    {
        var listings = new List<ResultItem>();

        for (int i = 0; i < resultIds.Count && listings.Count < maxWanted; i += TradeFetchBatchSize)
        {
            if (ct.IsCancellationRequested || !_bulkBuyInProgress) break;

            var batch = resultIds.Skip(i).Take(TradeFetchBatchSize).ToArray();

            await WaitForSearchQuotaAsync("fetch", ct);

            string url = $"https://www.pathofexile.com/api/trade/fetch/{string.Join(",", batch)}?query={Uri.EscapeDataString(queryId)}";

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                ApplyTradeHeaders(request, league, sessionId);

                using (var response = await _httpClient.SendAsync(request, ct))
                {
                    if (_rateLimiter != null)
                        await _rateLimiter.HandleRateLimitResponse(response);

                    string body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        LogError($"BulkBuy: Fetch failed for {batch.Length} listing(s) — {(int)response.StatusCode} {response.StatusCode}: {Truncate(body, 200)}");
                        continue;
                    }

                    ItemFetchResponse fetched;
                    try
                    {
                        fetched = JsonConvert.DeserializeObject<ItemFetchResponse>(body);
                    }
                    catch (Exception ex)
                    {
                        LogError($"BulkBuy: Could not parse a fetch response — {ex.Message}");
                        continue;
                    }

                    if (fetched?.Result == null) continue;

                    foreach (var result in fetched.Result)
                    {
                        if (result?.Listing == null || result.Item == null) continue;
                        listings.Add(result);
                        if (listings.Count >= maxWanted) break;
                    }
                }
            }
        }

        return listings;
    }

    /// <summary>
    /// A search that has been run, holding its ordered candidate ids and the listings fetched from
    /// them so far.
    ///
    /// Candidates are fetched a batch at a time rather than all at once, because the target is a
    /// number of items *bought*, and how many candidates that takes isn't known in advance —
    /// listings sell, fail their checks, or turn out to be from offline sellers. Fetching lazily
    /// also keeps hideout tokens young, since they're minted by the fetch.
    /// </summary>
    private sealed class SearchPlan
    {
        public string Name;
        public string League;
        public string QueryId;
        public string[] CandidateIds = Array.Empty<string>();
        public int TotalMatched;

        /// <summary>How far through <see cref="CandidateIds"/> the fetching has got.</summary>
        public int NextCandidate;

        /// <summary>How many items this search is meant to buy.</summary>
        public int Target;

        public int Bought;
        public int Considered;

        /// <summary>
        /// Fetched listings not yet attempted, in search order. A list rather than a queue because
        /// same-seller entries get pulled out of the middle to be bought in one visit.
        /// </summary>
        public readonly List<PendingPurchase> Ready = new List<PendingPurchase>();

        /// <summary>Takes the next listing, or null when none are ready.</summary>
        public PendingPurchase TakeNext()
        {
            if (Ready.Count == 0) return null;
            var next = Ready[0];
            Ready.RemoveAt(0);
            return next;
        }

        /// <summary>
        /// Removes and returns up to <paramref name="limit"/> further listings from the same seller,
        /// so they can be bought without travelling again.
        /// </summary>
        public List<PendingPurchase> TakeSameSeller(string account, int limit)
        {
            var taken = new List<PendingPurchase>();
            if (limit <= 0 || string.IsNullOrWhiteSpace(account)) return taken;

            for (int i = 0; i < Ready.Count && taken.Count < limit; )
            {
                if (string.Equals(Ready[i].SellerAccount, account, StringComparison.OrdinalIgnoreCase))
                {
                    taken.Add(Ready[i]);
                    Ready.RemoveAt(i);
                }
                else i++;
            }

            return taken;
        }

        public bool WantsMore => Bought < Target;
        public bool CandidatesLeft => NextCandidate < CandidateIds.Length;
    }

    /// <summary>
    /// Orders a seller's listings so the trade window is paged across as few times as possible:
    /// everything in <paramref name="openTab"/> first, then the rest grouped by tab.
    ///
    /// Only the sequence changes, never which listings get bought — the caller has already capped
    /// the list at what the target allows. LINQ's ordering is stable, so cheapest-first survives
    /// within each tab.
    /// </summary>
    private static List<PendingPurchase> OrderForFewestTabSwitches(List<PendingPurchase> items, string openTab)
    {
        return items
            .OrderByDescending(i => !string.IsNullOrWhiteSpace(openTab) &&
                                    string.Equals(TabOf(i), openTab, StringComparison.OrdinalIgnoreCase))
            .ThenBy(i => TabOf(i) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string TabOf(PendingPurchase item) => item?.Listing?.Listing?.Stash?.Name?.Trim();

    /// <summary>Whether two listings are priced identically.</summary>
    private static bool SamePrice(PendingPurchase a, PendingPurchase b)
    {
        var left = a?.Listing?.Listing?.Price;
        var right = b?.Listing?.Listing?.Price;
        if (left == null || right == null) return false;

        return left.Amount == right.Amount &&
               string.Equals(left.Currency, right.Currency, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>How many extra 10-listing batches to pull looking for more of one seller's stock.</summary>
    private const int MaxSellerLookaheadBatches = 5;

    /// <summary>
    /// Collects everything this seller has, not just what happened to land in the current batch.
    ///
    /// Listings are fetched ten at a time, so a seller with thirty copies at one price gets cut off
    /// at the batch boundary and only the first few are bought. Results are price-sorted, so while
    /// the tail of the queue is still at the same price as the item being bought, the band hasn't
    /// ended and there may be more of this seller's stock in it — keep pulling batches.
    ///
    /// Bounded two ways: by what the target still allows, and by
    /// <see cref="MaxSellerLookaheadBatches"/>, so a search where everything shares one price can't
    /// turn into fetching the entire result set.
    /// </summary>
    private async Task<List<PendingPurchase>> GatherSameSellerAsync(
        SearchPlan plan,
        PendingPurchase pending,
        string sessionId,
        CancellationToken ct)
    {
        var gathered = new List<PendingPurchase>();

        // One less than the shortfall: the listing being bought right now already covers one of it.
        int wanted = plan.Target - plan.Bought - 1;
        if (wanted <= 0) return gathered;

        gathered.AddRange(plan.TakeSameSeller(pending.SellerAccount, wanted));

        for (int batch = 0; batch < MaxSellerLookaheadBatches; batch++)
        {
            int room = wanted - gathered.Count;
            if (room <= 0 || !plan.CandidatesLeft) break;
            if (!_bulkBuyInProgress || ct.IsCancellationRequested) break;

            // An empty queue means we can't see where the price band ends, so look further;
            // otherwise stop as soon as the tail has fallen out of the band.
            var tail = plan.Ready.Count > 0 ? plan.Ready[plan.Ready.Count - 1] : pending;
            if (!SamePrice(tail, pending)) break;

            if (await TopUpSearchPlanAsync(plan, sessionId, ct) == 0) break;

            gathered.AddRange(plan.TakeSameSeller(pending.SellerAccount, room));
        }

        return gathered;
    }

    /// <summary>
    /// Runs a configured search and returns its candidate list, without fetching listing detail yet.
    /// Returns null when the search couldn't be run at all.
    /// </summary>
    private async Task<SearchPlan> PrepareSearchAsync(
        BulkBuySearch search,
        int target,
        string sessionId,
        CancellationToken ct)
    {
        string searchName = search.Name?.Value ?? "(unnamed)";

        string url = search.TradeUrl?.Value?.Trim() ?? "";
        string rawJson = search.QueryJson?.Value?.Trim() ?? "";

        JObject query = null;
        JToken sort = null;
        string league = null;

        if (!string.IsNullOrWhiteSpace(url))
        {
            if (!TryParseTradeUrl(url, out var urlLeague, out var searchId))
            {
                LogError($"BulkBuy: '{searchName}' — that doesn't look like a trade search link. " +
                         "Expected something like https://www.pathofexile.com/trade/search/Allflame/kyRr67a3u5");
                return null;
            }

            // The link carries its own league, and it is authoritative: the search id only exists
            // within that league. This is what stops BulkBuy inheriting the old hardcoded league.
            league = !string.IsNullOrWhiteSpace(urlLeague)
                ? urlLeague
                : (!string.IsNullOrWhiteSpace(search.League?.Value) ? search.League.Value.Trim() : ResolveLeague());

            // Remember the id so the GUI can show what a link resolved to.
            if (search.SearchId != null) search.SearchId.Value = searchId;

            LogMessage($"BulkBuy: '{searchName}' — loading search {searchId} from league '{league}'...");

            query = await ResolveSavedSearchQueryAsync(league, searchId, sessionId, ct);
            if (query == null) return null;
        }
        else if (!string.IsNullOrWhiteSpace(rawJson))
        {
            // Advanced path: a raw POST body pasted from the browser. Kept so existing configs
            // still run, but it is no longer what people are told to use.
            league = !string.IsNullOrWhiteSpace(search.League?.Value)
                ? search.League.Value.Trim()
                : ResolveLeague();

            try
            {
                var parsed = JObject.Parse(rawJson);
                // A pasted body is usually the whole {"query":..,"sort":..} envelope, but people
                // also paste just the inner query. Accept both.
                query = parsed["query"] as JObject ?? parsed;
                sort = parsed["sort"];
            }
            catch (Exception ex)
            {
                LogError($"BulkBuy: '{searchName}' — the Query JSON isn't valid JSON ({ex.Message}). " +
                         "Paste the trade search link into Trade URL instead; that's the easy path.");
                return null;
            }
        }
        else
        {
            LogError($"BulkBuy: '{searchName}' has no Trade URL set, so there is nothing to buy. " +
                     "Paste the trade search link into its Trade URL field.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(league))
        {
            LogError($"BulkBuy: '{searchName}' — could not work out which league to search. {LeagueUnresolvedReason()}.");
            return null;
        }

        var searchResponse = await RunTradeSearchAsync(league, query, sort, sessionId, ct);
        if (searchResponse?.Result == null || searchResponse.Result.Length == 0)
        {
            LogMessage($"BulkBuy: '{searchName}' matched nothing right now (league '{league}').");
            return null;
        }

        LogMessage($"BulkBuy: '{searchName}' — buying up to {target}, " +
                   $"{searchResponse.Total} listing(s) match ({searchResponse.Result.Length} reachable, cheapest first).");

        return new SearchPlan
        {
            Name = searchName,
            League = league,
            QueryId = searchResponse.Id,
            CandidateIds = searchResponse.Result,
            TotalMatched = searchResponse.Total,
            Target = target
        };
    }

    /// <summary>
    /// Fetches the next batch of candidates into the plan's ready queue.
    ///
    /// Listings with no seller account or no hideout token are dropped here rather than queued and
    /// skipped later — they were never buyable, so counting them against the target would be the
    /// same mistake as counting sold listings against it.
    /// </summary>
    /// <returns>How many buyable listings were added.</returns>
    private async Task<int> TopUpSearchPlanAsync(SearchPlan plan, string sessionId, CancellationToken ct)
    {
        int added = 0;

        while (added == 0 && plan.CandidatesLeft && !ct.IsCancellationRequested && _bulkBuyInProgress)
        {
            var batch = plan.CandidateIds
                .Skip(plan.NextCandidate)
                .Take(TradeFetchBatchSize)
                .ToArray();

            plan.NextCandidate += batch.Length;

            var listings = await FetchListingsAsync(plan.League, batch, plan.QueryId, sessionId, batch.Length, ct);

            foreach (var listing in listings)
            {
                var info = listing.Listing;
                var item = listing.Item;

                string account = info.Account?.Name;
                if (string.IsNullOrWhiteSpace(account))
                {
                    LogDebug($"BulkBuy: ignoring listing {listing.Id} — no seller account on it.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(info.HideoutToken))
                {
                    LogDebug($"BulkBuy: ignoring '{DescribeItem(item)}' from {account} — seller is offline.");
                    continue;
                }

                var (_, tokenExpiresAt) = RecentItem.ParseTokenTimes(info.HideoutToken);

                plan.Ready.Add(new PendingPurchase
                {
                    Listing = listing,
                    SearchName = plan.Name,
                    League = plan.League,
                    SellerAccount = account,
                    DisplayName = DescribeItem(item),
                    PriceText = info.Price != null ? $"{info.Price.Amount} {info.Price.Currency}" : "unpriced",
                    StashX = info.Stash?.X ?? 0,
                    StashY = info.Stash?.Y ?? 0,
                    HideoutToken = info.HideoutToken,
                    ItemId = listing.Id,
                    TokenExpiresAt = tokenExpiresAt
                });

                added++;
            }
        }

        return added;
    }

    private enum RefreshResult
    {
        /// <summary>Listing is alive; token and stash position have been updated.</summary>
        Refreshed,

        /// <summary>The trade API no longer has this listing — genuinely sold or delisted.</summary>
        Gone,

        /// <summary>Couldn't tell (network, quota, parse). Don't conclude anything from it.</summary>
        Failed
    }

    /// <summary>
    /// Re-fetches a single listing to mint a fresh hideout token and pick up its current stash
    /// position.
    ///
    /// This also settles a question the plugin previously guessed at. A whisper request for a sold
    /// listing and one with an expired token fail identically, so both were being reported as
    /// "listing sold" — inflating the skip count with items that were still sitting there. Re-fetch
    /// answers it directly: the fetch endpoint returns an empty result for a listing that is gone
    /// and a populated one for a listing that is merely stale.
    /// </summary>
    private async Task<RefreshResult> RefreshListingAsync(
        PendingPurchase pending,
        string sessionId,
        CancellationToken ct)
    {
        try
        {
            await WaitForSearchQuotaAsync("listing refresh", ct);

            string url = $"https://www.pathofexile.com/api/trade/fetch/{Uri.EscapeDataString(pending.ItemId)}";

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                ApplyTradeHeaders(request, pending.League, sessionId);

                using (var response = await _httpClient.SendAsync(request, ct))
                {
                    if (_rateLimiter != null)
                        await _rateLimiter.HandleRateLimitResponse(response);

                    string body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        LogDebug($"BulkBuy: refresh for {pending.ItemId} returned {(int)response.StatusCode}");
                        return RefreshResult.Failed;
                    }

                    ItemFetchResponse fetched;
                    try
                    {
                        fetched = JsonConvert.DeserializeObject<ItemFetchResponse>(body);
                    }
                    catch (Exception ex)
                    {
                        LogDebug($"BulkBuy: could not parse the refresh for {pending.ItemId} — {ex.Message}");
                        return RefreshResult.Failed;
                    }

                    // An empty result is the API saying the listing no longer exists. Note this is
                    // specifically an empty array, not a null payload, which would be a parse issue.
                    var result = fetched?.Result?.FirstOrDefault(r => r?.Listing != null);
                    if (result == null)
                        return RefreshResult.Gone;

                    var info = result.Listing;
                    if (string.IsNullOrWhiteSpace(info.HideoutToken))
                    {
                        // Listing is there but the seller went offline, so there's no way to reach
                        // them. Not sold, but not buyable either.
                        LogDebug($"BulkBuy: {pending.DisplayName} refreshed without a token (seller offline).");
                        return RefreshResult.Failed;
                    }

                    var (_, expiresAt) = RecentItem.ParseTokenTimes(info.HideoutToken);

                    pending.HideoutToken = info.HideoutToken;
                    pending.TokenExpiresAt = expiresAt;

                    // The seller may have shuffled their tab since the original search; take the
                    // current coordinates and item snapshot so verification checks against what is
                    // actually there now.
                    if (info.Stash != null)
                    {
                        pending.StashX = info.Stash.X;
                        pending.StashY = info.Stash.Y;
                    }

                    if (result.Item != null)
                        pending.Listing = result;

                    return RefreshResult.Refreshed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogDebug($"BulkBuy: refresh for {pending.ItemId} errored — {ex.Message}");
            return RefreshResult.Failed;
        }
    }

    /// <summary>
    /// A human-readable name for a fetched item: "The Light of Meaning" for uniques,
    /// the base type otherwise.
    /// </summary>
    private static string DescribeItem(Item item)
    {
        if (item == null) return "(unknown item)";
        if (!string.IsNullOrWhiteSpace(item.Name)) return item.Name.Trim();
        if (!string.IsNullOrWhiteSpace(item.TypeLine)) return item.TypeLine.Trim();
        if (!string.IsNullOrWhiteSpace(item.BaseType)) return item.BaseType.Trim();
        return "(unnamed item)";
    }

    /// <summary>
    /// Headers shared by every trade read request. The read path identifies itself honestly —
    /// spoofing a browser here buys nothing, since these are the same public endpoints the trade
    /// site serves to anyone.
    /// </summary>
    private void ApplyTradeHeaders(HttpRequestMessage request, string league, string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
            request.Headers.Add("Cookie", $"POESESSID={sessionId}");

        request.Headers.Add("User-Agent", PluginUserAgent);
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        request.Headers.Add("Referer", $"https://www.pathofexile.com/trade/search/{Uri.EscapeDataString(league ?? "")}");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
    }

    /// <summary>Longest a single request will sit waiting for its rate-limit window to reset.</summary>
    private const int MaxQuotaWaitMs = 30_000;

    /// <summary>
    /// Waits until <paramref name="scope"/> has quota again, and says so on screen while it does.
    ///
    /// Polls rather than sleeping for the full reset interval. The old version did one blind
    /// <c>Task.Delay(min(timeUntilReset, 30s))</c>, which meant a request whose window reset after
    /// two seconds still sat there for thirty — and with the status line untouched the whole time,
    /// so a token refresh wait followed by a teleport wait showed as one silent 60-second
    /// "Travelling to <seller>". Polling returns the instant the window opens.
    /// </summary>
    /// <returns>true if there is quota to spend now.</returns>
    private async Task<bool> WaitForQuotaAsync(string scope, string label, CancellationToken ct)
    {
        if (_rateLimiter == null) return true;
        if (_rateLimiter.CanMakeRequest(scope)) return true;

        string statusBefore = _bulkBuyStatus;
        var deadline = DateTime.Now.AddMilliseconds(MaxQuotaWaitMs);

        LogMessage($"BulkBuy: {label} is rate limited — {_rateLimiter.GetStatus(scope)}");

        try
        {
            while (DateTime.Now < deadline && _bulkBuyInProgress && !ct.IsCancellationRequested)
            {
                if (_rateLimiter.CanMakeRequest(scope)) return true;

                int secondsLeft = Math.Max(0, (int)(deadline - DateTime.Now).TotalSeconds);
                _bulkBuyStatus = $"Rate limited ({label}) — waiting up to {secondsLeft}s";

                await Task.Delay(500, ct);
            }

            return _rateLimiter.CanMakeRequest(scope);
        }
        finally
        {
            _bulkBuyStatus = statusBefore;
        }
    }

    /// <summary>
    /// Quota gate for the read path. Search and fetch deliberately share the limiter's default
    /// "account" bucket, as they always have here — splitting them into separate buckets would start
    /// each one empty and let the pair burst past the shared IP budget the trade site is policed on.
    ///
    /// Proceeds even if the wait times out: the request then gets a 429, which
    /// <c>HandleRateLimitResponse</c> accounts for properly. Blocking forever would be worse.
    /// </summary>
    private async Task WaitForSearchQuotaAsync(string label, CancellationToken ct)
    {
        if (!await WaitForQuotaAsync("account", label, ct))
            LogMessage($"BulkBuy: still rate limited after {MaxQuotaWaitMs / 1000}s; trying the {label} anyway.");
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\n", " ").Replace("\r", " ").Trim();
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }
}
