using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ExileCore.Shared.Nodes;
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
                {
                    _rateLimiter.ParseRateLimitHeaders(response);
                    await _rateLimiter.HandleRateLimitResponse(response);
                }

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
    /// Confirms the POESESSID still works, by asking GGG whose account it is.
    ///
    /// Worth one request at the start of a run because an expired session doesn't fail loudly: the
    /// search and fetch calls still answer 200, they just come back without hideout tokens. Every
    /// listing then looks like an offline seller and the run reports "ran out of listings", which
    /// sends you looking at the search instead of the session.
    /// </summary>
    /// <returns>The account name, or null if the session is no longer valid.</returns>
    private async Task<string> VerifyTradeSessionAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://www.pathofexile.com/api/profile"))
            {
                ApplyTradeHeaders(request, null, sessionId);

                using (var response = await _httpClient.SendAsync(request, ct))
                {
                    string body = await response.Content.ReadAsStringAsync();

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                        response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                        return null;

                    if (!response.IsSuccessStatusCode)
                    {
                        // Inconclusive, so the run continues — but it goes in the transcript,
                        // because "couldn't check" is a clue when the run then finds nothing.
                        BulkLog($"couldn't verify the session ({(int)response.StatusCode} {response.StatusCode}); " +
                                "carrying on, but treat a run that buys nothing as suspicious.", isError: true);
                        return "";
                    }

                    var name = JObject.Parse(body)["name"]?.ToString();
                    return string.IsNullOrWhiteSpace(name) ? null : name;
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"BulkBuy: couldn't check the session ({ex.Message}); carrying on.");
            return "";
        }
    }

    /// <summary>
    /// Fills a search's filter boxes in from what its trade link actually contains.
    ///
    /// The filters aren't in the URL — a trade link is just an id — so this costs one request, and
    /// it is therefore driven by a button rather than run automatically for every configured search
    /// at startup, which is precisely the burst that gets rate limited.
    ///
    /// Fire-and-forget from the settings renderer; the fields update when it lands.
    /// </summary>
    internal void LoadSearchFiltersFromLink(BulkBuySearch search)
    {
        if (search == null) return;

        string name = search.Name?.Value ?? "(unnamed)";
        string url = search.TradeUrl?.Value?.Trim() ?? "";

        if (!TryParseTradeUrl(url, out var urlLeague, out var searchId))
        {
            LogError($"BulkBuy: '{name}' — set a valid Trade URL before loading its filters.");
            return;
        }

        string league = !string.IsNullOrWhiteSpace(urlLeague)
            ? urlLeague
            : (!string.IsNullOrWhiteSpace(search.League?.Value) ? search.League.Value.Trim() : ResolveLeague());

        _ = Task.Run(async () =>
        {
            try
            {
                var sessionId = Settings.LiveSearch.SessionId?.Value ?? "";
                var query = await ResolveSavedSearchQueryAsync(league, searchId, sessionId, CancellationToken.None);
                if (query == null) return;

                var summary = new List<string>();

                var tradeFilters = ReadFilterGroup(query, "trade_filters");
                int max = tradeFilters?["price"]?["max"]?.Value<int?>() ?? 0;
                if (search.MaxPriceChaos != null)
                {
                    search.MaxPriceChaos.Value = Math.Max(0, Math.Min(1_000_000, max));
                    summary.Add(max > 0 ? $"max price {max}c" : "no price cap");
                }

                var misc = ReadFilterGroup(query, "misc_filters");
                summary.Add($"corrupted {ReadOptionInto(misc, "corrupted", search.CorruptedFilter)}");
                summary.Add($"identified {ReadOptionInto(misc, "identified", search.IdentifiedFilter)}");

                LogMessage($"BulkBuy: '{name}' — loaded from the link: {string.Join(", ", summary)}.");
            }
            catch (Exception ex)
            {
                LogError($"BulkBuy: couldn't load '{name}' filters from its link — {ex.Message}");
            }
        });
    }

    /// <summary>Reads a yes/no filter into a node and returns what it was set to.</summary>
    private static string ReadOptionInto(JObject group, string name, ListNode node)
    {
        string option = group?[name]?["option"]?.ToString();

        string value = option == null
            ? BulkBuySearch.FilterAny
            : (string.Equals(option, "true", StringComparison.OrdinalIgnoreCase)
                ? BulkBuySearch.FilterYes
                : BulkBuySearch.FilterNo);

        if (node != null) node.Value = value;
        return value.ToLowerInvariant();
    }

    /// <summary>Reads <c>filters.&lt;group&gt;.filters</c> without creating anything.</summary>
    private static JObject ReadFilterGroup(JObject query, string group) =>
        (query?["filters"] as JObject)?[group]?["filters"] as JObject;

    /// <summary>
    /// Applies the search's filter overrides to the query recovered from its trade link.
    ///
    /// Saves regenerating a link on the trade site every time a price moves. The shapes written here
    /// are the ones the site itself produces — <c>filters.trade_filters.filters.price.max</c> and
    /// <c>filters.misc_filters.filters.&lt;name&gt;.option</c> with a "true"/"false" string — so the
    /// result is a query GGG would have built.
    ///
    /// Read-modify-write throughout: setting a max price must not drop a min the link already had.
    /// </summary>
    /// <returns>A description of what was changed, or null if the link was left alone.</returns>
    private static string ApplySearchOverrides(JObject query, BulkBuySearch search)
    {
        if (query == null || search == null) return null;

        var applied = new List<string>();

        int maxPrice = search.MaxPriceChaos?.Value ?? 0;
        if (maxPrice > 0)
        {
            var tradeFilters = EnsureFilterGroup(query, "trade_filters");
            var price = tradeFilters["price"] as JObject ?? new JObject();
            price["max"] = maxPrice;
            tradeFilters["price"] = price;
            applied.Add($"max price {maxPrice}c");
        }

        ApplyOptionOverride(query, "corrupted", search.CorruptedFilter?.Value, applied);
        ApplyOptionOverride(query, "identified", search.IdentifiedFilter?.Value, applied);

        return applied.Count == 0 ? null : string.Join(", ", applied);
    }

    /// <summary>
    /// Writes one yes/no/any filter into <c>misc_filters</c>.
    ///
    /// "Any" is not the same as leaving the link alone: it actively removes the filter so both
    /// states match, which is the only way to widen a link that already narrows one.
    /// </summary>
    private static void ApplyOptionOverride(JObject query, string name, string mode, List<string> applied)
    {
        if (string.IsNullOrWhiteSpace(mode) || mode == BulkBuySearch.FilterFromLink) return;

        var misc = EnsureFilterGroup(query, "misc_filters");

        if (mode == BulkBuySearch.FilterAny)
        {
            if (misc.Remove(name)) applied.Add($"{name}: any");
            return;
        }

        bool wanted = mode == BulkBuySearch.FilterYes;
        misc[name] = new JObject { ["option"] = wanted ? "true" : "false" };
        applied.Add($"{name}: {(wanted ? "yes" : "no")}");
    }

    /// <summary>
    /// Returns <c>filters.&lt;group&gt;.filters</c>, creating the path if the link didn't have it.
    /// </summary>
    private static JObject EnsureFilterGroup(JObject query, string group)
    {
        if (!(query["filters"] is JObject filters))
        {
            filters = new JObject();
            query["filters"] = filters;
        }

        if (!(filters[group] is JObject groupObject))
        {
            groupObject = new JObject();
            filters[group] = groupObject;
        }

        if (!(groupObject["filters"] is JObject inner))
        {
            inner = new JObject();
            groupObject["filters"] = inner;
        }

        return inner;
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
                {
                    _rateLimiter.ParseRateLimitHeaders(response);
                    await _rateLimiter.HandleRateLimitResponse(response);
                }

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

            await WaitForFetchQuotaAsync("fetch", ct);

            string url = $"https://www.pathofexile.com/api/trade/fetch/{string.Join(",", batch)}?query={Uri.EscapeDataString(queryId)}";

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                ApplyTradeHeaders(request, league, sessionId);

                using (var response = await _httpClient.SendAsync(request, ct))
                {
                    if (_rateLimiter != null)
                    {
                        _rateLimiter.ParseRateLimitHeaders(response);
                        await _rateLimiter.HandleRateLimitResponse(response);
                    }

                    string body = await response.Content.ReadAsStringAsync();

                    if (!response.IsSuccessStatusCode)
                    {
                        // Cloudflare's challenge page, not GGG's rate limiter. It carries no
                        // X-Rate-Limit headers and its Retry-After is an HTTP-date, so there is
                        // nothing to pace against — the only correct response is to stop.
                        bool challenged = body.Contains("Just a moment") || body.Contains("cf-browser-verification");

                        BulkLog(challenged
                                ? "Cloudflare is challenging our requests (HTTP 429, \"Just a moment\"). " +
                                  "That's the site itself refusing, not GGG's rate limit — nothing can be " +
                                  "fetched until it lets up. Give it several minutes."
                                : $"couldn't fetch {batch.Length} listing(s) — {(int)response.StatusCode} " +
                                  $"{response.StatusCode}: {Truncate(body, 200)}",
                                isError: true);

                        _lastFetchFailed = true;
                        _fetchBlocked = challenged || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
                        return listings;
                    }

                    _lastFetchFailed = false;
                    _fetchBlocked = false;

                    ItemFetchResponse fetched;
                    try
                    {
                        fetched = JsonConvert.DeserializeObject<ItemFetchResponse>(body);
                    }
                    catch (Exception ex)
                    {
                        // Through BulkLog and treated as a blocking failure. This went to LogError
                        // and then `continue`d, which meant a response the plugin couldn't read
                        // discarded ten listings per batch without leaving a trace anywhere — the
                        // run just reported "ran out of listings" having silently thrown away
                        // everything the search found.
                        BulkLog($"couldn't read the fetch response — {ex.Message}. " +
                                "The trade API's format has probably changed; the listings in this " +
                                "batch were discarded, not skipped.", isError: true);

                        _lastFetchFailed = true;
                        _fetchBlocked = true;
                        return listings;
                    }

                    int entries = fetched?.Result?.Length ?? 0;
                    int usable = 0, blank = 0;

                    if (fetched?.Result != null)
                    {
                        foreach (var result in fetched.Result)
                        {
                            // The trade API returns a null entry for an id that has since
                            // disappeared. This used to be a bare `continue` with nothing counting
                            // it, so a batch that came back entirely blank was indistinguishable
                            // from one that was never requested.
                            if (result?.Listing == null || result.Item == null)
                            {
                                blank++;
                                continue;
                            }

                            usable++;
                            listings.Add(result);
                            if (listings.Count >= maxWanted) break;
                        }
                    }


                    _fetchBlankEntries += blank;

                    if (usable == 0)
                    {
                        BulkLog($"fetched {batch.Length} listing id(s) — the API returned {entries} entr" +
                                $"{(entries == 1 ? "y" : "ies")}, {blank} of them empty, 0 usable.",
                                isError: true);
                    }
                    else
                    {
                        LogDebug($"BulkBuy: fetched {batch.Length} id(s) — {usable} usable, {blank} empty.");
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
        /// Candidates thrown away before they could be tried. Counted because a run that discards
        /// every listing looks identical to a search that matched nothing, and the two have
        /// completely different causes — the usual one being an expired POESESSID, since the fetch
        /// silently omits hideout tokens when the session isn't valid.
        /// </summary>
        public int DroppedNoToken;
        public int DroppedNoAccount;

        /// <summary>Ids the API answered with an empty entry — the listing is gone.</summary>
        public int BlankEntries;

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

        // Applied after the query is recovered and before it is run, so it covers both the trade
        // link and the raw Query JSON path.
        var overrides = ApplySearchOverrides(query, search);
        if (overrides != null)
            LogMessage($"BulkBuy: '{searchName}' — overriding the link with {overrides}.");

        var searchResponse = await RunTradeSearchAsync(league, query, sort, sessionId, ct);
        if (searchResponse?.Result == null || searchResponse.Result.Length == 0)
        {
            LogMessage($"BulkBuy: '{searchName}' matched nothing right now (league '{league}').");
            return null;
        }

        // In the transcript, not just the debug window: "total" and "how many ids we actually got
        // back" are different numbers, and confusing them is how a search with 95 matches ends up
        // with nothing to fetch.
        BulkLog($"'{searchName}': search returned {searchResponse.Result.Length} listing id(s) " +
                $"out of {searchResponse.Total} total; buying up to {target}.");

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
    /// <summary>Set when a fetch came back non-OK, so the caller can stop instead of grinding on.</summary>
    private bool _lastFetchFailed;

    /// <summary>
    /// Set when the last fetch was refused rather than merely unlucky — a 429, or Cloudflare's
    /// challenge page. Retrying that achieves nothing except more refusals, so the run stops.
    /// </summary>
    internal bool _fetchBlocked;

    /// <summary>Running total of empty entries the API returned, sampled per batch by the caller.</summary>
    private int _fetchBlankEntries;


    private async Task<int> TopUpSearchPlanAsync(SearchPlan plan, string sessionId, CancellationToken ct)
    {
        int added = 0;
        _lastFetchFailed = false;

        LogDebug($"BulkBuy: topping up '{plan.Name}' — at candidate {plan.NextCandidate} of " +
                 $"{plan.CandidateIds.Length}, {plan.Ready.Count} ready.");

        if (!plan.CandidatesLeft)
        {
            BulkLog($"'{plan.Name}': no candidates to fetch — the search gave " +
                    $"{plan.CandidateIds.Length} id(s) and {plan.NextCandidate} have been used.",
                    isError: true);
            return 0;
        }

        while (added == 0 && plan.CandidatesLeft && !ct.IsCancellationRequested && _bulkBuyInProgress)
        {
            var batch = plan.CandidateIds
                .Skip(plan.NextCandidate)
                .Take(TradeFetchBatchSize)
                .ToArray();

            plan.NextCandidate += batch.Length;

            int blankBefore = _fetchBlankEntries;
            var listings = await FetchListingsAsync(plan.League, batch, plan.QueryId, sessionId, batch.Length, ct);
            plan.BlankEntries += _fetchBlankEntries - blankBefore;

            foreach (var listing in listings)
            {
                var info = listing.Listing;
                var item = listing.Item;

                string account = info.Account?.Name;
                if (string.IsNullOrWhiteSpace(account))
                {
                    plan.DroppedNoAccount++;
                    LogDebug($"BulkBuy: ignoring listing {listing.Id} — no seller account on it.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(info.HideoutToken))
                {
                    plan.DroppedNoToken++;
                    LogDebug($"BulkBuy: ignoring '{DescribeItem(item)}' from {account} — no hideout token.");
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

            // A failed fetch means the request didn't happen, not that these listings were no good.
            // Carrying on would march through the whole candidate list burning it on requests that
            // are all failing for the same reason — which is how a rate limit turned into
            // "ran out of listings, 0 tried" with no explanation.
            if (_lastFetchFailed)
            {
                plan.NextCandidate -= batch.Length;
                BulkLog("stopping this search's fetching — the listings weren't retrieved, so they " +
                        "haven't been used up.", isError: true);
                break;
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
                    {
                        _rateLimiter.ParseRateLimitHeaders(response);
                        await _rateLimiter.HandleRateLimitResponse(response);
                    }

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

    /// <summary>GGG's own policy names, which are what the limiter keys its buckets on.</summary>
    private const string SearchPolicy = "trade-search-request-limit";
    private const string FetchPolicy = "trade-fetch-request-limit";

    /// <summary>
    /// Quota gate for the read path.
    ///
    /// Search and fetch are policed separately and very differently — 5 per 10s with a 60s penalty
    /// against 12 per 4s with a 10s one — so they are waited on separately. Sharing one bucket meant
    /// neither number was right: a long run's fetches were blocked by the search allowance while
    /// nothing tracked the fetch allowance at all.
    ///
    /// Proceeds even if the wait times out: the request then gets a 429, which
    /// <c>HandleRateLimitResponse</c> accounts for properly. Blocking forever would be worse.
    /// </summary>
    private async Task WaitForSearchQuotaAsync(string label, CancellationToken ct) =>
        await WaitForPolicyQuotaAsync(SearchPolicy, label, ct);

    private async Task WaitForFetchQuotaAsync(string label, CancellationToken ct) =>
        await WaitForPolicyQuotaAsync(FetchPolicy, label, ct);

    private async Task WaitForPolicyQuotaAsync(string policy, string label, CancellationToken ct)
    {
        if (!await WaitForQuotaAsync(policy, label, ct))
            LogMessage($"BulkBuy: still rate limited after {MaxQuotaWaitMs / 1000}s; trying the {label} anyway.");
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\n", " ").Replace("\r", " ").Trim();
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }
}
