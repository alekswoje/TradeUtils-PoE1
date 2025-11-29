using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using System.Text;
using TradeUtils.Utility;
using TradeUtils.Models;

namespace TradeUtils;

public partial class TradeUtils
{
    // BulkBuy-specific fields
    private Queue<BulkBuyItem> _bulkBuyQueue = new Queue<BulkBuyItem>();
    private BulkBuyItem _currentBulkBuyItem = null;
    private bool _bulkBuyInProgress = false;
    private bool _waitingForPurchaseWindow = false;
    private DateTime _bulkBuyLastPurchaseTime = DateTime.MinValue;
    private DateTime _bulkBuyPurchaseWindowOpenTime = DateTime.MinValue;
    private DateTime _bulkBuyStartTime = DateTime.MinValue;
    private DateTime _lastBulkBuyActionTime = DateTime.MinValue;
    private int _bulkBuyRetryCount = 0;
    private decimal _totalSpent = 0;
    private Task _bulkBuyTask;
    private System.Threading.CancellationTokenSource _bulkBuyCts;
    private bool _lastBulkBuyStartHotkeyState = false;
    private bool _bulkBuyPausedForFocus = false;
    
    partial void InitializeBulkBuy()
    {
        try
        {
            LogMessage("BulkBuy sub-plugin initialized");

            if (_rateLimiter == null)
            {
                _rateLimiter = new QuotaGuard(LogMessage, LogError, () => LiveSearchSettings);
            }

            // Validate BulkBuy session id (POESESSID)
            var bulkSession = Settings.BulkBuy.SessionId?.Value ?? "";
            if (string.IsNullOrWhiteSpace(bulkSession))
            {
                LogMessage("❌ BulkBuy: POESESSID is empty. Set it in Bulk Buy Settings > General > POESESSID (BulkBuy).");
            }
            else
            {
                LogMessage($"BulkBuy: Using dedicated POESESSID (length {bulkSession.Length}).");
            }

            int totalGroups = Settings.BulkBuy.Groups.Count;
            int totalSearches = Settings.BulkBuy.Groups.SelectMany(g => g.Searches).Count();

            LogMessage($"BulkBuy config: Groups={totalGroups}, Searches={totalSearches}");
        }
        catch (Exception ex)
        {
            LogError($"Failed to initialize BulkBuy: {ex.Message}");
        }
    }

    partial void RenderBulkBuy()
    {
        RenderBulkBuyGui();
    }

    partial void AreaChangeBulkBuy(AreaInstance area)
    {
        // BulkBuy doesn't need area change handling currently
    }

    partial void DisposeBulkBuy()
    {
        try
        {
            // Clean up any bulk buy resources
            _bulkBuyQueue?.Clear();
        }
        catch (Exception ex)
        {
            LogError($"Error disposing BulkBuy: {ex.Message}");
        }
    }

    partial void TickBulkBuy()
    {
        try
        {
            if (!GameController.Window.IsForeground())
            {
                if (_bulkBuyInProgress)
                {
                    if (!_bulkBuyPausedForFocus)
                    {
                        LogMessage("BulkBuy: Game not focused, pausing (will auto-resume when focused).");
                        _bulkBuyPausedForFocus = true;
                    }
                }
                return;
            }

            if (_bulkBuyInProgress && _bulkBuyPausedForFocus)
            {
                LogMessage("BulkBuy: Game focused again, resuming.");
                _bulkBuyPausedForFocus = false;
            }

            if (Settings.BulkBuy.General != null)
            {
                var toggleKey = Settings.BulkBuy.General.ToggleHotkey?.Value ?? Keys.None;
                if (toggleKey != Keys.None)
                {
                    bool currentStartState = Input.GetKeyState(toggleKey);
                    if (currentStartState && !_lastBulkBuyStartHotkeyState)
                    {
                        if (_bulkBuyInProgress)
                        {
                            LogMessage("BulkBuy: Toggle hotkey pressed - stopping bulk buy");
                            StopBulkBuy();
                        }
                        else
                        {
                            LogMessage($"BulkBuy: Toggle hotkey pressed ({toggleKey}), starting bulk buying...");
                            StartBulkBuy();
                        }
                    }

                    _lastBulkBuyStartHotkeyState = currentStartState;
                }
            }

            if (BulkBuySettings.StopAllHotkey.Value != Keys.None && Input.GetKeyState(BulkBuySettings.StopAllHotkey.Value))
            {
                StopBulkBuy();
            }

            // BulkBuy-specific purchase window handling (independent of LiveSearch enable/state)
            var pw = GameController.IngameState.IngameUi.PurchaseWindowHideout;
            bool pwVisible = pw != null && pw.IsVisible;

            if (pwVisible && !_lastPurchaseWindowVisible)
            {
                LogMessage($"BulkBuy: Purchase window opened. CurrentItem={_currentBulkBuyItem?.Name ?? "null"}, Coords=({_currentBulkBuyItem?.X},{_currentBulkBuyItem?.Y})");

                if (_currentBulkBuyItem != null)
                {
                    MoveMouseToItemLocation(_currentBulkBuyItem.X, _currentBulkBuyItem.Y);
                }
                else if (_teleportedItemLocation.X != 0 || _teleportedItemLocation.Y != 0)
                {
                    LogMessage($"BulkBuy: No current item, falling back to teleported coords ({_teleportedItemLocation.X},{_teleportedItemLocation.Y})");
                    MoveMouseToItemLocation(_teleportedItemLocation.X, _teleportedItemLocation.Y);
                }
            }

            _lastPurchaseWindowVisible = pwVisible;
        }
        catch (Exception ex)
        {
            LogError($"Error in BulkBuy tick: {ex.Message}");
        }
    }
    
    private void StartBulkBuy()
    {
        try
        {
            if (_bulkBuyInProgress)
            {
                LogMessage("BulkBuy: Start requested but already in progress");
                return;
            }

            if (!Settings.Enable.Value)
            {
                LogMessage("BulkBuy: Plugin is disabled - cannot start");
                return;
            }

            // Make sure we have a valid session id (use BulkBuy's own POESESSID field)
            var sessionId = Settings.BulkBuy.SessionId?.Value ?? "";
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                LogError("BulkBuy: Cannot start - POESESSID is empty. Configure it in Bulk Buy Settings > General > POESESSID (BulkBuy).");
                return;
            }

            var activeSearches = Settings.BulkBuy.Groups
                .Where(g => g.Enable.Value)
                .SelectMany(g => g.Searches.Where(s => s.Enable.Value))
                .ToList();

            if (activeSearches.Count == 0)
            {
                LogMessage("BulkBuy: No enabled searches found. Enable some groups and searches in BulkBuy settings first.");
                return;
            }

            _bulkBuyQueue.Clear();
            _currentBulkBuyItem = null;
            _bulkBuyRetryCount = 0;
            _totalSpent = 0;
            Settings.BulkBuy.TotalItemsProcessed = 0;
            Settings.BulkBuy.SuccessfulPurchases = 0;
            Settings.BulkBuy.FailedPurchases = 0;
            Settings.BulkBuy.CurrentItemIndex = 0;

            _bulkBuyStartTime = DateTime.Now;
            _bulkBuyLastPurchaseTime = DateTime.MinValue;
            _lastBulkBuyActionTime = DateTime.Now;

            _bulkBuyCts?.Cancel();
            _bulkBuyCts = new System.Threading.CancellationTokenSource();

            _bulkBuyInProgress = true;
            Settings.BulkBuy.IsRunning = true;

            LogMessage($"BulkBuy: Starting with {activeSearches.Count} enabled searches");

            // Run the main loop on a background task
            _bulkBuyTask = Task.Run(() => RunBulkBuyLoopAsync(activeSearches, sessionId, _bulkBuyCts.Token));
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: Error starting bulk buy - {ex.Message}");
            _bulkBuyInProgress = false;
            Settings.BulkBuy.IsRunning = false;
        }
    }
    
    private void StopBulkBuy()
    {
        try
        {
            if (!_bulkBuyInProgress)
            {
                LogMessage("BulkBuy: Stop requested but not currently running");
            }

            _bulkBuyInProgress = false;
            _bulkBuyPausedForFocus = false;
            Settings.BulkBuy.IsRunning = false;

            try
            {
                _bulkBuyCts?.Cancel();
            }
            catch
            {
                // ignore
            }

            _bulkBuyQueue?.Clear();
            _currentBulkBuyItem = null;

            LogMessage("BulkBuy: Stopped");
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: Error while stopping - {ex.Message}");
        }
    }

    /// <summary>
    /// Main BulkBuy processing loop. Iterates through all enabled searches,
    /// calls the trade search API, and processes items one by one.
    /// </summary>
    private async Task RunBulkBuyLoopAsync(
        System.Collections.Generic.List<BulkBuySearch> activeSearches,
        string sessionId,
        System.Threading.CancellationToken ct)
    {
        try
        {
            // No global max; enforcement is per-search via BulkBuySearch.MaxItems
            int globalRemaining = int.MaxValue;

            foreach (var search in activeSearches)
            {
                if (ct.IsCancellationRequested) break;
                if (!_bulkBuyInProgress) break;

                if (!search.Enable.Value) continue; // Skip if disabled mid-run

                // Per-search max
                int perSearchMax = search.MaxItems?.Value ?? 0;
                if (perSearchMax <= 0) perSearchMax = int.MaxValue;

                int remainingForSearch = Math.Min(globalRemaining, perSearchMax);
                if (remainingForSearch <= 0)
                    break;

                LogMessage($"BulkBuy: Processing search '{search.Name.Value}' with limit {remainingForSearch} items (JSON query mode, league='Keepers')");

                int purchasedFromSearch = await ProcessBulkBuyForSearchAsync(search, remainingForSearch, sessionId, ct);

                globalRemaining -= purchasedFromSearch;

                if (globalRemaining <= 0)
                {
                    LogMessage("BulkBuy: Reached global max items limit, stopping.");
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage("BulkBuy: Cancelled.");
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: Unhandled error in main loop - {ex.Message}");
        }
        finally
        {
            _bulkBuyInProgress = false;
            Settings.BulkBuy.IsRunning = false;
        }
    }

    /// <summary>
    /// Executes a single BulkBuy search: fetches result ids, then processes items one-by-one.
    /// </summary>
    private async Task<int> ProcessBulkBuyForSearchAsync(
        BulkBuySearch search,
        int maxItemsForSearch,
        string sessionId,
        System.Threading.CancellationToken ct)
    {
        int purchasedCount = 0;

        try
        {
            // Safety: ensure we have a JSON body
            if (string.IsNullOrWhiteSpace(search.QueryJson?.Value))
            {
                LogError($"BulkBuy: Search '{search.Name.Value}' has empty Query JSON, skipping.");
                return 0;
            }

            // Loop until we've bought maxItemsForSearch, or there are no more items, or cancelled
            while (purchasedCount < maxItemsForSearch && !ct.IsCancellationRequested && _bulkBuyInProgress)
            {
                // Check inventory before each search pass
                if (IsInventoryFullFor2x4Item())
                {
                    LogMessage("BulkBuy: Inventory is full (no 2x4 space). Stopping bulk buy.");
                    StopBulkBuy();
                    break;
                }

                // Respect rate limit (shared QuotaGuard)
                if (_rateLimiter != null && !_rateLimiter.CanMakeRequest())
                {
                    LogMessage($"BulkBuy: Quota too low before search request - {_rateLimiter.GetStatus()}");
                    int waitMs = _rateLimiter.GetTimeUntilReset();
                    if (waitMs > 0)
                    {
                        await Task.Delay(Math.Min(waitMs, 30_000), ct);
                    }
                }

                // POST /api/trade/search/{league} with raw JSON body (normal trade search flow)
                string league = string.IsNullOrWhiteSpace(search.League?.Value) ? "Keepers" : search.League.Value;
                string searchUrl = $"https://www.pathofexile.com/api/trade/search/{league}";

                var body = search.QueryJson.Value.Trim();

                using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, searchUrl))
                {
                    request.Headers.Add("Cookie", $"POESESSID={sessionId}");
                    request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36");
                    request.Headers.Add("Accept", "*/*");
                    request.Headers.Add("Accept-Encoding", "gzip, deflate, br, zstd");
                    request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                    request.Headers.Add("Priority", "u=1, i");
                    request.Headers.Add("Referer", $"https://www.pathofexile.com/trade/search/{league}");
                    request.Headers.Add("X-Requested-With", "XMLHttpRequest");
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    using (var response = await _httpClient.SendAsync(request, ct))
                    {
                        if (_rateLimiter != null)
                        {
                            await _rateLimiter.HandleRateLimitResponse(response);
                        }

                        if (!response.IsSuccessStatusCode)
                        {
                            string err = await response.Content.ReadAsStringAsync();
                            LogError($"BulkBuy: Search request failed for '{search.Name.Value}' - {response.StatusCode}: {err}");

                            if (Settings.BulkBuy.StopOnError.Value)
                            {
                                LogMessage("BulkBuy: StopOnError is enabled - stopping.");
                                StopBulkBuy();
                                return purchasedCount;
                            }

                            // On error but without StopOnError, just break out of this search.
                            break;
                        }

                        string json = await response.Content.ReadAsStringAsync();
                        TradeSearchResponse searchResponse;
                        try
                        {
                            searchResponse = JsonConvert.DeserializeObject<TradeSearchResponse>(json);
                        }
                        catch (Exception ex)
                        {
                            LogError($"BulkBuy: Failed to parse search response for '{search.Name.Value}' - {ex.Message}");
                            break;
                        }

                        if (searchResponse == null || searchResponse.Result == null || searchResponse.Result.Length == 0)
                        {
                            LogMessage($"BulkBuy: No items found for search '{search.Name.Value}'.");
                            break;
                        }

                        LogMessage($"BulkBuy: Search '{search.Name.Value}' returned {searchResponse.Result.Length} items (total={searchResponse.Total}).");

                        // Process items in batches of up to 10 IDs per fetch request
                        var allIds = searchResponse.Result;
                        const int maxPerBatch = 10;

                        for (int i = 0; i < allIds.Length && purchasedCount < maxItemsForSearch && _bulkBuyInProgress && !ct.IsCancellationRequested; i += maxPerBatch)
                        {
                            var batchIds = allIds.Skip(i).Take(maxPerBatch).ToArray();
                            int purchasedFromBatch = await ProcessBulkBuyBatchAsync(
                                search,
                                batchIds,
                                searchResponse.Id,
                                sessionId,
                                maxItemsForSearch - purchasedCount,
                                ct);

                            purchasedCount += purchasedFromBatch;

                            if (purchasedFromBatch == 0 && Settings.BulkBuy.StopOnError.Value)
                            {
                                // In strict mode, any batch failure stops the entire run
                                LogMessage("BulkBuy: StopOnError is enabled - stopping due to batch failure.");
                                StopBulkBuy();
                                return purchasedCount;
                            }

                            if (purchasedCount >= maxItemsForSearch ||
                                IsInventoryFullFor2x4Item() ||
                                !_bulkBuyInProgress ||
                                ct.IsCancellationRequested)
                            {
                                break;
                            }
                        }
                    }
                }

                // For now, run search only once; if we want to keep re-running until empty we can loop again.
                break;
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage($"BulkBuy: Search '{search.Name.Value}' cancelled.");
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: Error in search '{search.Name.Value}' - {ex.Message}");
        }

        return purchasedCount;
    }

    /// <summary>
    /// Processes a batch of up to 10 item ids: fetches them in one request and
    /// performs teleport + (optional) auto-buy for each item using the existing logic,
    /// respecting the remaining per-search limit.
    /// </summary>
    private async Task<int> ProcessBulkBuyBatchAsync(
        BulkBuySearch search,
        string[] itemIds,
        string queryId,
        string sessionId,
        int remainingAllowed,
        System.Threading.CancellationToken ct)
    {
        int purchasedInBatch = 0;

        try
        {
            if (ct.IsCancellationRequested || !_bulkBuyInProgress)
                return 0;

            if (_rateLimiter != null && !_rateLimiter.CanMakeRequest())
            {
                LogMessage($"BulkBuy: Quota too low before fetch - {_rateLimiter.GetStatus()}");
                int waitMs = _rateLimiter.GetTimeUntilReset();
                if (waitMs > 0)
                {
                    await Task.Delay(Math.Min(waitMs, 30_000), ct);
                }
            }

            // Use query id returned from POST /api/trade/search/Keepers, batching up to 10 ids
            string idsJoined = string.Join(",", itemIds);
            string fetchUrl = $"https://www.pathofexile.com/api/trade/fetch/{idsJoined}?query={queryId}";

            using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, fetchUrl))
            {
                request.Headers.Add("Cookie", $"POESESSID={sessionId}");
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36");
                request.Headers.Add("Accept", "*/*");
                request.Headers.Add("Accept-Encoding", "gzip, deflate, br, zstd");
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                request.Headers.Add("Priority", "u=1, i");
                request.Headers.Add("Referer", "https://www.pathofexile.com/trade/search/Keepers");
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");

                using (var response = await _httpClient.SendAsync(request, ct))
                {
                    if (_rateLimiter != null)
                    {
                        await _rateLimiter.HandleRateLimitResponse(response);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        string err = await response.Content.ReadAsStringAsync();
                        LogError($"BulkBuy: Fetch failed for batch [{idsJoined}] - {response.StatusCode}: {err}");
                        return 0;
                    }

                    string json = await response.Content.ReadAsStringAsync();
                    ItemFetchResponse fetchResponse;
                    try
                    {
                        fetchResponse = JsonConvert.DeserializeObject<ItemFetchResponse>(json);
                    }
                    catch (Exception ex)
                    {
                        LogError($"BulkBuy: Failed to parse fetch response for batch [{idsJoined}] - {ex.Message}");
                        return 0;
                    }

                    if (fetchResponse?.Result == null || fetchResponse.Result.Length == 0)
                    {
                        LogMessage($"BulkBuy: No items returned for batch [{idsJoined}] (likely sold).");
                        return 0;
                    }

                    foreach (var resultItem in fetchResponse.Result)
                    {
                        if (ct.IsCancellationRequested || !_bulkBuyInProgress)
                            break;

                        if (purchasedInBatch >= remainingAllowed)
                            break;

                        bool success = await ProcessFetchedBulkBuyItemAsync(resultItem, search, ct);

                        if (success)
                        {
                            purchasedInBatch++;
                            Settings.BulkBuy.TotalItemsProcessed++;
                            Settings.BulkBuy.SuccessfulPurchases++;
                            Settings.BulkBuy.CurrentItemIndex++;
                        }
                        else
                        {
                            Settings.BulkBuy.TotalItemsProcessed++;
                            Settings.BulkBuy.FailedPurchases++;
                        }
                    }

                    return purchasedInBatch;
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogMessage("BulkBuy: Batch processing cancelled.");
            return purchasedInBatch;
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: Error while processing batch [{string.Join(",", itemIds)}] - {ex.Message}");
            return purchasedInBatch;
        }
    }

    /// <summary>
    /// Takes a fetched ResultItem and performs teleport + (optional) auto-buy
    /// using the existing LiveSearch teleport/mouse logic.
    /// </summary>
    private async Task<bool> ProcessFetchedBulkBuyItemAsync(
        ResultItem itemModel,
        BulkBuySearch search,
        System.Threading.CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested || !_bulkBuyInProgress)
                return false;

            var listing = itemModel.Listing;
            var poeItem = itemModel.Item;

            if (listing == null || poeItem == null)
            {
                LogMessage($"BulkBuy: Incomplete listing for item {itemModel.Id}, skipping.");
                return false;
            }

            string name = string.IsNullOrEmpty(poeItem.Name) ? poeItem.TypeLine : poeItem.Name;
            string priceStr = listing.Price != null
                ? $"{listing.Price.Amount} {listing.Price.Currency}"
                : "Unknown";

            LogMessage($"BulkBuy: Preparing to buy '{name}' for {priceStr} at ({listing.Stash?.X},{listing.Stash?.Y})");

            // Build BulkBuyItem and translate to RecentItem so we can reuse
            // existing teleport + auto-buy pipeline.
            var bulkItem = new BulkBuyItem
            {
                Name = name,
                Price = priceStr,
                HideoutToken = listing.HideoutToken,
                ItemId = itemModel.Id,
                SearchId = search.SearchId.Value,
                AccountName = listing.Account?.Name ?? "",
                IsOnline = listing.Account?.Online != null,
                X = listing.Stash?.X ?? 0,
                Y = listing.Stash?.Y ?? 0,
                AddedTime = DateTime.Now,
                Status = "Pending"
            };

            _currentBulkBuyItem = bulkItem;

            // TODO: hook in proper gold check once we have a reliable method.
            // For now, we assume the player can afford it.

            // Push into recent items queue and use teleport/auto-buy logic.
            var (issuedAt, expiresAt) = RecentItem.ParseTokenTimes(bulkItem.HideoutToken);
            var recent = new RecentItem
            {
                Name = bulkItem.Name,
                Price = bulkItem.Price,
                HideoutToken = bulkItem.HideoutToken,
                ItemId = bulkItem.ItemId,
                SearchId = bulkItem.SearchId,
                X = bulkItem.X,
                Y = bulkItem.Y,
                AddedTime = bulkItem.AddedTime,
                TokenIssuedAt = issuedAt,
                TokenExpiresAt = expiresAt
            };

            lock (_recentItemsLock)
            {
                _recentItems.Enqueue(recent);
            }

            LogMessage($"BulkBuy: Teleporting to seller '{bulkItem.AccountName}' for item '{bulkItem.Name}'");

            _allowMouseMovement = true;
            _windowWasClosedSinceLastMovement = true;

            _forceAutoBuy = true;
            _lastTeleportSucceeded = false;
            TravelToHideout(isManual: false);

            int timeoutSec = Settings.BulkBuy.TimeoutPerItem.Value;
            DateTime startWait = DateTime.Now;

            while (!ct.IsCancellationRequested &&
                   (DateTime.Now - startWait).TotalSeconds < timeoutSec)
            {
                await Task.Delay(250, ct);
            }

            _forceAutoBuy = false;

            if (!_lastTeleportSucceeded)
            {
                LogMessage($"BulkBuy: Teleport for item {itemModel.Id} did not succeed, treating as failed.");
                return false;
            }

            _currentBulkBuyItem.Status = "Completed";

            // Update spent counter if we have a numeric amount
            if (listing.Price != null)
            {
                _totalSpent += listing.Price.Amount;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            LogMessage($"BulkBuy: Item {itemModel.Id} processing cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: Error while processing item {itemModel.Id} - {ex.Message}");
            return false;
        }
    }
}

// BulkBuy item model
public class BulkBuyItem
{
    public string Name { get; set; }
    public string Price { get; set; }
    public string HideoutToken { get; set; }
    public string ItemId { get; set; }
    public string SearchId { get; set; }
    public string AccountName { get; set; }
    public bool IsOnline { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public DateTime AddedTime { get; set; }
    public string Status { get; set; } = "Pending";
}
