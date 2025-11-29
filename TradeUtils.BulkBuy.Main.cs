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
    private int _bulkBuyCurrencyFailureCount = 0;
    
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
            _bulkBuyCurrencyFailureCount = 0;
            _bulkBuyCtrlHeld = false;
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

            // Release Ctrl if it's being held
            if (_bulkBuyCtrlHeld)
            {
                try
                {
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    _bulkBuyCtrlHeld = false;
                    LogMessage("BulkBuy: Released Ctrl key");
                }
                catch (Exception ex)
                {
                    LogError($"BulkBuy: Error releasing Ctrl key: {ex.Message}");
                }
            }

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
            // Always release Ctrl if it's being held
            if (_bulkBuyCtrlHeld)
            {
                try
                {
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    _bulkBuyCtrlHeld = false;
                    LogMessage("BulkBuy: Released Ctrl key (loop ended)");
                }
                catch (Exception ex)
                {
                    LogError($"BulkBuy: Error releasing Ctrl key in finally: {ex.Message}");
                }
            }
            
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

                    // Group items by seller account name
                    var itemsBySeller = fetchResponse.Result
                        .Where(r => r.Listing?.Account != null && !string.IsNullOrEmpty(r.Listing.Account.Name))
                        .GroupBy(r => r.Listing.Account.Name)
                        .ToList();

                    if (itemsBySeller.Count == 0)
                    {
                        LogMessage("BulkBuy: No items with valid seller accounts in batch.");
                        return 0;
                    }

                    LogMessage($"BulkBuy: Processing {itemsBySeller.Count} seller(s) with {fetchResponse.Result.Length} total item(s)");

                    // Process each seller's items
                    foreach (var sellerGroup in itemsBySeller)
                    {
                        if (ct.IsCancellationRequested || !_bulkBuyInProgress)
                            break;

                        if (purchasedInBatch >= remainingAllowed)
                            break;

                        if (_bulkBuyCurrencyFailureCount >= 2)
                        {
                            LogMessage("BulkBuy: Stopping due to currency failures (2 items failed to purchase after 5 attempts each).");
                            StopBulkBuy();
                            return purchasedInBatch;
                        }

                        int purchasedFromSeller = await ProcessSellerItemsAsync(
                            sellerGroup.Key,
                            sellerGroup.ToList(),
                            search,
                            sessionId,
                            remainingAllowed - purchasedInBatch,
                            ct);

                        purchasedInBatch += purchasedFromSeller;

                        if (purchasedInBatch >= remainingAllowed)
                            break;
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
    /// Processes all items from a single seller. Teleports once, then processes each item
    /// by sending hideout tokens, waiting for purchase window, and attempting to buy.
    /// </summary>
    private async Task<int> ProcessSellerItemsAsync(
        string sellerAccountName,
        List<ResultItem> sellerItems,
        BulkBuySearch search,
        string sessionId,
        int remainingAllowed,
        System.Threading.CancellationToken ct)
    {
        int purchasedFromSeller = 0;
        bool firstItem = true;

        try
        {
            LogMessage($"BulkBuy: Processing {sellerItems.Count} item(s) from seller '{sellerAccountName}'");

            foreach (var itemModel in sellerItems)
            {
                if (ct.IsCancellationRequested || !_bulkBuyInProgress)
                    break;

                if (purchasedFromSeller >= remainingAllowed)
                    break;

                if (_bulkBuyCurrencyFailureCount >= 2)
                {
                    LogMessage("BulkBuy: Stopping due to currency failures.");
                    StopBulkBuy();
                    return purchasedFromSeller;
                }

                var listing = itemModel.Listing;
                var poeItem = itemModel.Item;

                if (listing == null || poeItem == null)
                {
                    LogMessage($"BulkBuy: Incomplete listing for item {itemModel.Id}, skipping.");
                    continue;
                }

                string name = string.IsNullOrEmpty(poeItem.Name) ? poeItem.TypeLine : poeItem.Name;
                string priceStr = listing.Price != null
                    ? $"{listing.Price.Amount} {listing.Price.Currency}"
                    : "Unknown";

                LogMessage($"BulkBuy: Preparing to buy '{name}' for {priceStr} at ({listing.Stash?.X},{listing.Stash?.Y})");

                var bulkItem = new BulkBuyItem
                {
                    Name = name,
                    Price = priceStr,
                    HideoutToken = listing.HideoutToken,
                    ItemId = itemModel.Id,
                    SearchId = search.SearchId.Value,
                    AccountName = sellerAccountName,
                    IsOnline = listing.Account?.Online != null,
                    X = listing.Stash?.X ?? 0,
                    Y = listing.Stash?.Y ?? 0,
                    AddedTime = DateTime.Now,
                    Status = "Pending"
                };

                _currentBulkBuyItem = bulkItem;

                bool success = false;

                if (firstItem)
                {
                    // First item: use full teleport
                    LogMessage($"BulkBuy: Teleporting to seller '{sellerAccountName}' for item '{bulkItem.Name}'");
                    
                    // Press and hold Ctrl for the entire seller session
                    if (!_bulkBuyCtrlHeld)
                    {
                        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                        _bulkBuyCtrlHeld = true;
                        await Task.Delay(50, ct); // Wait for Ctrl to register
                        LogMessage("BulkBuy: Pressed and holding Ctrl for seller session");
                    }
                    
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

                    _allowMouseMovement = true;
                    _windowWasClosedSinceLastMovement = true;
                    _forceAutoBuy = true;
                    _lastTeleportSucceeded = false;
                    TravelToHideout(isManual: false);

                    // Wait for teleport to complete
                    int teleportWaitMs = 0;
                    while (!_lastTeleportSucceeded && teleportWaitMs < 5000 && !ct.IsCancellationRequested && _bulkBuyInProgress)
                    {
                        await Task.Delay(100, ct);
                        teleportWaitMs += 100;
                    }

                    if (!_lastTeleportSucceeded)
                    {
                        LogMessage($"BulkBuy: Teleport for item {itemModel.Id} did not succeed, treating as failed.");
                        Settings.BulkBuy.TotalItemsProcessed++;
                        Settings.BulkBuy.FailedPurchases++;
                        continue;
                    }

                    firstItem = false;
                }
                else
                {
                    // Subsequent items: send hideout token to switch tabs
                    LogMessage($"BulkBuy: Sending hideout token for item '{bulkItem.Name}' to switch to correct tab");
                    
                    // Create RecentItem for MoveMouseToItemLocation to find
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
                    
                    // Set teleported item info so MoveMouseToItemLocation can find it
                    _teleportedItemInfo = recent;
                    
                    bool tokenSent = await SendHideoutTokenForItemAsync(bulkItem, sessionId, ct);
                    if (!tokenSent)
                    {
                        LogMessage($"BulkBuy: Failed to send hideout token for item {itemModel.Id}, skipping.");
                        Settings.BulkBuy.TotalItemsProcessed++;
                        Settings.BulkBuy.FailedPurchases++;
                        _teleportedItemInfo = null;
                        continue;
                    }

                    // Wait for tab switch (150-250ms)
                    await Task.Delay(200, ct);
                }

                // Wait for purchase window to open (max 3 seconds)
                bool windowOpened = await WaitForPurchaseWindowAsync(3000, ct);
                if (!windowOpened)
                {
                    LogMessage($"BulkBuy: Purchase window did not open within 3 seconds for item '{bulkItem.Name}', skipping.");
                    Settings.BulkBuy.TotalItemsProcessed++;
                    Settings.BulkBuy.FailedPurchases++;
                    continue;
                }

                // Try to buy the item (up to 5 attempts)
                success = await TryBuyItemWithRetriesAsync(bulkItem, 5, ct);

                if (success)
                {
                    purchasedFromSeller++;
                    Settings.BulkBuy.TotalItemsProcessed++;
                    Settings.BulkBuy.SuccessfulPurchases++;
                    Settings.BulkBuy.CurrentItemIndex++;
                    _currentBulkBuyItem.Status = "Completed";
                    _teleportedItemInfo = null; // Clear after successful purchase

                    if (listing.Price != null)
                    {
                        _totalSpent += listing.Price.Amount;
                    }
                }
                else
                {
                    Settings.BulkBuy.TotalItemsProcessed++;
                    Settings.BulkBuy.FailedPurchases++;
                    _bulkBuyCurrencyFailureCount++;
                    _teleportedItemInfo = null; // Clear after failed purchase
                    LogMessage($"BulkBuy: Failed to buy '{bulkItem.Name}' after 5 attempts (currency failure count: {_bulkBuyCurrencyFailureCount}/2)");
                }
            }

            // Release Ctrl when done with this seller
            if (_bulkBuyCtrlHeld)
            {
                try
                {
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    _bulkBuyCtrlHeld = false;
                    LogMessage("BulkBuy: Released Ctrl key (done with seller)");
                }
                catch (Exception ex)
                {
                    LogError($"BulkBuy: Error releasing Ctrl key: {ex.Message}");
                }
            }

            return purchasedFromSeller;
        }
        catch (OperationCanceledException)
        {
            LogMessage($"BulkBuy: Seller '{sellerAccountName}' processing cancelled.");
            return purchasedFromSeller;
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: Error processing seller '{sellerAccountName}' - {ex.Message}");
            return purchasedFromSeller;
        }
    }

    /// <summary>
    /// Sends a hideout token to switch to the correct tab for an item (used when already in hideout).
    /// </summary>
    private async Task<bool> SendHideoutTokenForItemAsync(
        BulkBuyItem item,
        string sessionId,
        System.Threading.CancellationToken ct)
    {
        try
        {
            if (_rateLimiter != null && !_rateLimiter.CanMakeRequest())
            {
                LogMessage($"BulkBuy: Quota too low before sending hideout token - {_rateLimiter.GetStatus()}");
                int waitMs = _rateLimiter.GetTimeUntilReset();
                if (waitMs > 0)
                {
                    await Task.Delay(Math.Min(waitMs, 30_000), ct);
                }
            }

            var request = new HttpRequestMessage(HttpMethod.Post, "https://www.pathofexile.com/api/trade/whisper")
            {
                Content = new StringContent($"{{ \"token\": \"{item.HideoutToken}\", \"continue\": true }}", Encoding.UTF8, "application/json")
            };

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
                    LogError($"BulkBuy: Hideout token request failed for item '{item.Name}' - {response.StatusCode}: {err}");
                    return false;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                bool success = !responseContent.Contains("\"error\"") && 
                              (responseContent.Contains("\"success\"") || response.StatusCode == System.Net.HttpStatusCode.OK);

                if (success)
                {
                    LogMessage($"BulkBuy: Hideout token sent successfully for item '{item.Name}'");
                }
                else
                {
                    LogError($"BulkBuy: Hideout token response indicates failure: {responseContent}");
                }

                return success;
            }
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: Error sending hideout token for item '{item.Name}' - {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Waits for the purchase window to become visible, with a maximum timeout.
    /// </summary>
    private async Task<bool> WaitForPurchaseWindowAsync(int timeoutMs, System.Threading.CancellationToken ct)
    {
        DateTime startTime = DateTime.Now;
        while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs && !ct.IsCancellationRequested && _bulkBuyInProgress)
        {
            try
            {
                var purchaseWindow = GameController.IngameState.IngameUi.PurchaseWindowHideout;
                if (purchaseWindow != null && purchaseWindow.IsVisible)
                {
                    return true;
                }
            }
            catch
            {
                // Ignore exceptions during window check
            }

            await Task.Delay(100, ct);
        }

        return false;
    }

    /// <summary>
    /// Attempts to buy an item with retries. Returns true if successful, false if all attempts failed.
    /// </summary>
    private async Task<bool> TryBuyItemWithRetriesAsync(
        BulkBuyItem item,
        int maxAttempts,
        System.Threading.CancellationToken ct)
    {
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested || !_bulkBuyInProgress)
                return false;

            string sigBefore = GetInventorySignature();
            if (string.IsNullOrEmpty(sigBefore))
            {
                LogMessage($"BulkBuy: Could not get inventory signature before attempt {attempt}, retrying...");
                await Task.Delay(500, ct);
                continue;
            }

            // Move mouse and click (MoveMouseToItemLocation handles clicking and second-click logic internally)
            _forceAutoBuy = true;
            _allowMouseMovement = true;
            _windowWasClosedSinceLastMovement = true;
            MoveMouseToItemLocation(item.X, item.Y);

            // Wait for MoveMouseToItemLocation to complete its work (it waits 1s for second click if needed)
            // So we wait 2.5s total to ensure both clicks and inventory update have time
            await Task.Delay(2500, ct);

            // Check multiple indicators of purchase success/failure
            bool purchased = false;
            for (int check = 0; check < 3; check++)
            {
                // Check 1: Inventory signature changed (item added to inventory)
                string sigAfter = GetInventorySignature();
                if (sigAfter != sigBefore)
                {
                    LogMessage($"BulkBuy: Successfully bought '{item.Name}' on attempt {attempt} (inventory changed)");
                    _forceAutoBuy = false;
                    return true;
                }

                // Check 2: Item is on cursor (indicates failed purchase - item was picked up but not bought)
                if (IsItemOnCursor())
                {
                    LogMessage($"BulkBuy: Item '{item.Name}' is on cursor after click, purchase failed. Dropping item...");
                    await DropItemFromCursorAsync();
                    // Continue to next check - this attempt failed
                    continue;
                }

                // Check 3: Item no longer exists in stash (purchase successful)
                if (!IsItemStillInStash(item.X, item.Y))
                {
                    LogMessage($"BulkBuy: Successfully bought '{item.Name}' on attempt {attempt} (item no longer in stash)");
                    _forceAutoBuy = false;
                    return true;
                }

                if (check < 2)
                {
                    await Task.Delay(500, ct);
                }
            }

            LogMessage($"BulkBuy: Attempt {attempt}/{maxAttempts} failed for '{item.Name}' (inventory unchanged)");
            
            if (attempt < maxAttempts)
            {
                await Task.Delay(500, ct); // Brief delay before retry
            }
        }

        _forceAutoBuy = false;
        return false;
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
