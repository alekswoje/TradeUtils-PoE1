using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using Newtonsoft.Json;
using NAudio.Wave;
using System.Media;
using TradeUtils.Utility;
using System.Threading;
using System.IO;
using System.Net;
using RectangleF = SharpDX.RectangleF;

namespace TradeUtils;

public partial class TradeUtils
{
    private async Task<bool> RefreshItemToken(RecentItem item)
    {
        try
        {
            LogDebug($"🔄 REFRESHING TOKEN: Fetching fresh token for item {item.ItemId}");
            
            if (_rateLimiter != null && !_rateLimiter.CanMakeRequest())
            {
                LogMessage($"⛔ QUOTA TOO LOW: Skipping token refresh - {_rateLimiter.GetStatus()}");
                return false;
            }
            
            var fetchUrl = $"https://www.pathofexile.com/api/trade/fetch/{item.ItemId}";
            
            using (var request = new HttpRequestMessage(HttpMethod.Get, fetchUrl))
            {
                var sessionId = GetPoeSessionForRequests();
                request.Headers.Add("Cookie", $"POESESSID={sessionId}");
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
                
                using (var response = await _httpClient.SendAsync(request))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        dynamic fetchResponse = JsonConvert.DeserializeObject(responseBody);
                        
                        if (fetchResponse?.result != null && fetchResponse.result.Count > 0)
                        {
                            var refreshedItem = fetchResponse.result[0];
                            string newToken = refreshedItem?.listing?.hideout_token;
                            
                            if (!string.IsNullOrEmpty(newToken))
                            {
                                var (issuedAt, expiresAt) = RecentItem.ParseTokenTimes(newToken);
                                
                                item.HideoutToken = newToken;
                                item.TokenIssuedAt = issuedAt;
                                item.TokenExpiresAt = expiresAt;
                                
                                LogInfo($"✅ TOKEN REFRESHED: New token expires at {expiresAt:HH:mm:ss}");
                                return true;
                            }
                        }
                    }
                    
                    LogWarning($"❌ TOKEN REFRESH FAILED: API returned {response.StatusCode}");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"❌ TOKEN REFRESH ERROR: {ex.Message}");
            return false;
        }
    }

    private async void TravelToHideout(bool isManual = false, bool allowRetry = true)
    {
        if (!Settings.Enable.Value)
        {
            LogDebug("🛑 Teleport blocked: Plugin is disabled");
            return;
        }
        
        _isManualTeleport = isManual;
        if (isManual)
        {
            LogInfo("🎯 MANUAL TELEPORT: User initiated teleport via hotkey");
        }
        else
        {
            LogDebug("🤖 AUTO TELEPORT: Auto teleport triggered by new item");
        }

        if (!this.GameController.Area.CurrentArea.IsHideout)
        {
            LogDebug("Teleport skipped: Not in hideout zone.");
            _isManualTeleport = false;
            _currentTeleportingItem = null;
            return;
        }

        if (GameController.IsLoading)
        {
            LogDebug("⏳ LOADING SCREEN: Game is loading, skipping teleport for safety");
            _isManualTeleport = false;
            _currentTeleportingItem = null;
            // If called from BulkBuy (allowRetry=false), remove the item from queue to prevent it from being processed later
            if (!allowRetry)
            {
                lock (_recentItemsLock)
                {
                    if (_recentItems.Count > 0)
                    {
                        _recentItems.Dequeue();
                    }
                }
            }
            return;
        }
        
        if (!GameController.InGame)
        {
            LogDebug("🚫 NOT IN GAME: Not in valid game state for teleporting");
            _isManualTeleport = false;
            _currentTeleportingItem = null;
            return;
        }

        LogDebug("=== TRAVEL TO HIDEOUT HOTKEY PRESSED ===");
        
        RecentItem currentItem;
        lock (_recentItemsLock)
        {
            LogDebug($"Recent items count: {_recentItems.Count}");
            
            if (_recentItems.Count == 0) 
            {
                LogDebug("No recent items available for travel");
                _isManualTeleport = false;
                _currentTeleportingItem = null;
                return;
            }

            currentItem = _recentItems.Peek();
        }
        
        if (currentItem.IsTokenExpired())
        {
            LogDebug($"🔄 TOKEN EXPIRED: Token for {currentItem.Name} expired at {currentItem.TokenExpiresAt:HH:mm:ss}, refreshing...");
            var refreshSuccess = await RefreshItemToken(currentItem);
            if (!refreshSuccess)
            {
                LogWarning("❌ TOKEN REFRESH FAILED: Unable to refresh token, removing item from queue");
                
                bool hasMoreItems;
                lock (_recentItemsLock)
                {
                    if (_recentItems.Count > 0)
                    {
                        _recentItems.Dequeue();
                    }
                    else
                    {
                        LogWarning("⚠️ RACE CONDITION: Queue became empty before dequeue attempt");
                        _isManualTeleport = false;
                        _currentTeleportingItem = null; // Clear current teleporting item
                        return;
                    }
                    
                    hasMoreItems = _recentItems.Count > 0;
                    if (hasMoreItems)
                    {
                        LogDebug($"🔄 RETRY: Attempting teleport to next item ({_recentItems.Count} remaining)");
                    }
                }
                
                if (hasMoreItems && allowRetry)
                {
                    await Task.Delay(500);
                    TravelToHideout(_isManualTeleport, allowRetry);
                    return;
                }
                else
                {
                    if (!allowRetry)
                    {
                        LogDebug("🛑 RETRY DISABLED: Not retrying (called from BulkBuy)");
                    }
                    else
                    {
                        LogDebug("No valid items remaining for teleport");
                    }
                    _isManualTeleport = false;
                    _currentTeleportingItem = null; // Clear current teleporting item
                    return;
                }
            }
        }
        
        LogDebug($"Attempting to travel to hideout for item: {currentItem.Name} - {currentItem.Price}");
        LogDebug($"Hideout token: {currentItem.HideoutToken}");
        
        _currentTeleportingItem = currentItem;
        _teleportedItemInfo = currentItem;
        var request = new HttpRequestMessage(HttpMethod.Post, "https://www.pathofexile.com/api/trade/whisper")
        {
            Content = new StringContent($"{{ \"token\": \"{currentItem.HideoutToken}\", \"continue\": true }}", Encoding.UTF8, "application/json")
        };
        var sessionId = GetPoeSessionForRequests();
        request.Headers.Add("Cookie", $"POESESSID={sessionId}");
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/139.0.0.0 Safari/537.36");
        request.Headers.Add("Accept", "*/*");
        request.Headers.Add("Accept-Encoding", "gzip, deflate, br, zstd");
        request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
        request.Headers.Add("Priority", "u=1, i");
        if (_activeListener != null)
        {
            request.Headers.Add("Referer", $"https://www.pathofexile.com/trade/search/poe2/{Uri.EscapeDataString(_activeListener.Config.League.Value)}/{_activeListener.Config.SearchId.Value}/live");
        }
        else
        {
            LogMessage("[WARNING] No active listener found for referer URL.");
        }
        request.Headers.Add("Sec-Ch-Ua", "\"Not;A=Brand\";v=\"99\", \"Google Chrome\";v=\"139\", \"Chromium\";v=\"139\"");
        request.Headers.Add("Sec-Ch-Ua-Arch", "x86");
        request.Headers.Add("Sec-Ch-Ua-Bitness", "64");
        request.Headers.Add("Sec-Ch-Ua-Full-Version", "139.0.7258.157");
        request.Headers.Add("Sec-Ch-Ua-Full-Version-List", "\"Not;A=Brand\";v=\"99.0.0.0\", \"Google Chrome\";v=\"139.0.7258.157\", \"Chromium\";v=\"139.0.7258.157\"");
        request.Headers.Add("Sec-Ch-Ua-Mobile", "?0");
        request.Headers.Add("Sec-Ch-Ua-Model", "");
        request.Headers.Add("Sec-Ch-Ua-Platform", "Windows");
        request.Headers.Add("Sec-Ch-Ua-Platform-Version", "19.0.0");
        request.Headers.Add("Sec-Fetch-Dest", "empty");
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        try
        {
            if (_rateLimiter == null)
            {
                _rateLimiter = new QuotaGuard(LogMessage, LogError, () => LiveSearchSettings);
            }
            
            // Use separate scope for teleport/whisper API rate limiting
            const string teleportScope = "whisper";
            
            if (_rateLimiter != null && !_rateLimiter.CanMakeRequest(teleportScope))
            {
                LogMessage($"⛔ TELEPORT QUOTA TOO LOW: Skipping teleport - {_rateLimiter.GetStatus(teleportScope)}");
                LogMessage($"⏳ Teleport quota resets in {_rateLimiter.GetTimeUntilReset(teleportScope) / 1000} seconds");
                return;
            }
            
            LogDebug("Sending teleport request...");
            
            _lastTeleportSucceeded = false;
            _lastTeleportItemExpired = false; // Reset expiration flag
            var response = await _httpClient.SendAsync(request);
            LogDebug($"Response status: {response.StatusCode}");
            LogDebug("📡 TELEPORT REQUEST: Sent successfully, waiting for response");
            
            if (_rateLimiter != null)
            {
                // Parse rate limit headers with teleport scope
                _rateLimiter.ParseRateLimitHeaders(response);
                
                var rateLimitWaitTime = await _rateLimiter.HandleRateLimitResponse(response);
                if (rateLimitWaitTime > 0)
                {
                    LogMessage($"🚨 TELEPORT RATE LIMITED! Waiting before retry...");
                    return; // Rate limited, wait and return
                }
            }
            
            if (!response.IsSuccessStatusCode)
            {
                LogError($"Teleport failed: {response.StatusCode}");
                var responseContent = await response.Content.ReadAsStringAsync();
                LogError($"Response content: {responseContent}");
                
                // Check if item expired (NotFound with "Resource not found" message)
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    if (responseContent.Contains("Resource not found") || responseContent.Contains("Item no longer available"))
                    {
                        _lastTeleportItemExpired = true; // Mark as expired
                        LogMessage($"🗑️ ITEM EXPIRED: Item '{currentItem.Name}' no longer available (expired)");
                    }
                }
                
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound || 
                    response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                    response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                    {
                        LogMessage($"🔄 SERVICE UNAVAILABLE: Token likely expired for '{currentItem.Name}', attempting refresh...");
                        var refreshSuccess = await RefreshItemToken(currentItem);
                        if (refreshSuccess && allowRetry)
                        {
                            LogMessage("✅ TOKEN REFRESHED: Retrying teleport with fresh token");
                            await Task.Delay(1000); // Short delay before retry
                            TravelToHideout(_isManualTeleport, allowRetry);
                            return;
                        }
                        else
                        {
                            if (!allowRetry)
                            {
                                LogMessage("🛑 RETRY DISABLED: Token refresh succeeded but retry disabled (BulkBuy)");
                            }
                            else
                            {
                                LogMessage("❌ TOKEN REFRESH FAILED: Removing item from queue");
                            }
                        }
                    }
                    else
                    {
                        LogMessage($"🗑️ ITEM EXPIRED: Removing expired item '{currentItem.Name}' and trying next...");
                    }
                    
                    if (!allowRetry)
                    {
                        LogMessage("🛑 RETRY DISABLED: Not retrying with next item (called from BulkBuy)");
                        _isManualTeleport = false;
                        _currentTeleportingItem = null;
                        lock (_recentItemsLock)
                        {
                            if (_recentItems.Count > 0)
                            {
                                _recentItems.Dequeue();
                            }
                        }
                        return;
                    }
                    
                    bool hasMoreItems;
                    lock (_recentItemsLock)
                    {
                        if (_recentItems.Count > 0)
                        {
                            _recentItems.Dequeue();
                        }
                        else
                        {
                            LogMessage("⚠️ RACE CONDITION: Queue became empty before dequeue");
                            _isManualTeleport = false; // Reset flag
                            _currentTeleportingItem = null; // Clear current teleporting item
                            return;
                        }
                        
                        hasMoreItems = _recentItems.Count > 0;
                        if (hasMoreItems)
                        {
                            LogDebug($"🔄 RETRY: Attempting teleport to next item ({_recentItems.Count} remaining)");
                        }
                    }
                    
                    if (hasMoreItems)
                    {
                        await Task.Delay(500);
                        TravelToHideout(_isManualTeleport, allowRetry);
                    }
                    else
                    {
                        LogMessage("📭 NO MORE ITEMS: All items in queue have expired");
                        _isManualTeleport = false;
                        _currentTeleportingItem = null;
                    }
                }
            }
            else
            {
                var responseContent = await response.Content.ReadAsStringAsync();
                LogDebug($"🔍 TELEPORT RESPONSE: Status={response.StatusCode}, Content={responseContent}");
                
                bool actualSuccess = !responseContent.Contains("\"error\"") && 
                                   !responseContent.Contains("failed") && 
                                   !responseContent.Contains("invalid");
                
                if (actualSuccess)
                {
                    LogInfo("✅ Teleport to hideout successful!");
                    _lastTeleportSucceeded = true;
                    
                    _teleportedItemLocation = (currentItem.X, currentItem.Y);
                    if (_isManualTeleport)
                    {
                        LogDebug($"📍 STORED TELEPORT LOCATION: Manual teleport to item at ({currentItem.X}, {currentItem.Y}) for mouse movement");
                    }
                    else
                    {
                        LogDebug($"📍 STORED TELEPORT LOCATION: Auto teleport to item at ({currentItem.X}, {currentItem.Y}) for mouse movement");
                    }
                    
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        try
                        {
                            LogDebug($"✅ TELEPORT VERIFICATION: Response validation passed, assuming successful teleport");
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"🔍 TELEPORT VERIFICATION: Could not verify area - {ex.Message}");
                        }
                    });
                }
                else
                {
                    LogWarning($"⚠️ TELEPORT RESPONSE INDICATES FAILURE: {responseContent}");
                    LogMessage($"❌ TELEPORT FAILED: API returned success but response indicates failure for '{currentItem.Name}'");
                    
                    if (!allowRetry)
                    {
                        LogMessage("🛑 RETRY DISABLED: Not retrying with next item (called from BulkBuy)");
                        _isManualTeleport = false;
                        _currentTeleportingItem = null;
                        lock (_recentItemsLock)
                        {
                            if (_recentItems.Count > 0)
                            {
                                _recentItems.Dequeue();
                            }
                        }
                        return;
                    }
                    
                    bool hasMoreItems;
                    lock (_recentItemsLock)
                    {
                        if (_recentItems.Count > 0)
                        {
                            _recentItems.Dequeue();
                        }
                        hasMoreItems = _recentItems.Count > 0;
                    }
                    
                    if (hasMoreItems)
                    {
                        LogDebug($"🔄 RETRY: Attempting teleport to next item ({_recentItems.Count} remaining)");
                        await Task.Delay(500);
                        TravelToHideout(_isManualTeleport, allowRetry);
                        return;
                    }
                    else
                    {
                        LogMessage("📭 NO MORE ITEMS: All items in queue have failed");
                        _isManualTeleport = false;
                        _currentTeleportingItem = null;
                    }
                }
                
                lock (_recentItemsLock)
                {
                    _recentItems.Clear();
                }
                _lastTpTime = DateTime.Now;
                _isManualTeleport = false;
                _currentTeleportingItem = null;
            }
        }
        catch (Exception ex)
        {
            LogError($"Teleport request failed: {ex.Message}");
            LogError($"Exception details: {ex}");
            
            _isManualTeleport = false;
            _currentTeleportingItem = null;
        }
    }

    public async Task PerformCtrlLeftClickAsync()
    {
        if (!Settings.Enable.Value)
        {
            LogMessage("🛑 Auto buy blocked: Plugin is disabled");
            return;
        }

        try
        {
            LogMessage("🖱️ AUTO BUY: Performing Ctrl+Left Click...");
            
            await Task.Delay(50);
            
            bool ctrlAlreadyHeld = _bulkBuyCtrlHeld;
            
            if (!ctrlAlreadyHeld)
            {
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                await Task.Delay(30);
            }
            
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(20);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            
            if (!ctrlAlreadyHeld)
            {
                await Task.Delay(30);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            
            // Note: Clipboard clearing removed due to STA thread requirement
            // The clipboard will be overwritten on next Ctrl+C anyway
            
            LogInfo("✅ AUTO BUY: Ctrl+Left Click completed!");
        }
        catch (Exception ex)
        {
            LogError($"❌ AUTO BUY FAILED: {ex.Message}");
            if (!_bulkBuyCtrlHeld)
            {
                try
                {
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Checks if there's an item on the cursor (indicates a failed purchase attempt).
    /// </summary>
    private bool IsItemOnCursor()
    {
        try
        {
            var playerInventories = GameController?.Game?.IngameState?.ServerData?.PlayerInventories;
            if (playerInventories == null) return false;
            
            foreach (var pi in playerInventories)
            {
                if (pi?.Inventory?.InventType == InventoryTypeE.Cursor)
                {
                    return pi.Inventory?.Items != null && pi.Inventory.Items.Count > 0;
                }
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Drops an item from the cursor by right-clicking.
    /// </summary>
    private async Task DropItemFromCursorAsync()
    {
        try
        {
            mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(10);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(200);
        }
        catch (Exception ex)
        {
            LogError($"Error dropping item from cursor: {ex.Message}");
        }
    }

    private bool IsItemStillInStash(int x, int y)
    {
        try
        {
            var purchaseWindow = GameController.IngameState.IngameUi.PurchaseWindowHideout;
            if (purchaseWindow == null || !purchaseWindow.IsVisible)
                return false;

            var npcInventories = GameController?.Game?.IngameState?.ServerData?.NPCInventories;
            if (npcInventories == null)
                return false;

            foreach (var npcInv in npcInventories)
            {
                if (npcInv?.Inventory?.InventorySlotItems == null)
                    continue;

                foreach (var slotItem in npcInv.Inventory.InventorySlotItems)
                {
                    if (slotItem != null && slotItem.PosX == x && slotItem.PosY == y)
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task MoveMouseToItemLocationAsync(int x, int y, System.Threading.CancellationToken ct = default)
    {
        if (!Settings.Enable.Value)
        {
            LogMessage("🛑 Mouse movement blocked: Plugin is disabled");
            return;
        }

        try
        {
            var purchaseWindow = GameController.IngameState.IngameUi.PurchaseWindowHideout;
            if (!purchaseWindow.IsVisible)
            {
                LogMessage("MoveMouseToItemLocation: Purchase window is not visible");
                return;
            }

            var stashPanel = purchaseWindow.TabContainer.StashInventoryPanel;
            if (stashPanel == null)
            {
                LogMessage("MoveMouseToItemLocation: Stash panel is null");
                return;
            }

            var rect = stashPanel.GetClientRectCache;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                LogMessage("MoveMouseToItemLocation: Invalid stash panel dimensions");
                return;
            }

            float cellWidth = rect.Width / 12.0f;
            float cellHeight = rect.Height / 12.0f;
            var topLeft = rect.TopLeft;
            
            int itemX = (int)(topLeft.X + (x * cellWidth) + (cellWidth * 7 / 8));
            int itemY = (int)(topLeft.Y + (y * cellHeight) + (cellHeight * 7 / 8));
            
            var windowRect = GameController.Window.GetWindowRectangle();
            System.Drawing.Point windowPos = new System.Drawing.Point((int)windowRect.X, (int)windowRect.Y);
            
            int finalX = windowPos.X + itemX;
            int finalY = windowPos.Y + itemY;
            
            LogDebug($"🎯 CALCULATION DEBUG: Input coords=({x},{y}), Panel=({rect.X},{rect.Y},{rect.Width},{rect.Height}), CellSize=({cellWidth},{cellHeight}), TopLeft=({topLeft.X},{topLeft.Y}), ItemPos=({itemX},{itemY}), Window=({windowPos.X},{windowPos.Y}), Final=({finalX},{finalY})");
            
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point(finalX, finalY);
            
            LogDebug($"Moved mouse to item location: Stash({x},{y}) -> Screen({finalX},{finalY}) - Panel size: {rect.Width}x{rect.Height}");
        }
        catch (Exception ex)
        {
            LogError($"MoveMouseToItemLocationAsync failed: {ex.Message}");
        }
    }

    private async void MoveMouseToItemLocation(int x, int y, bool skipClick = false)
    {
        if (!Settings.Enable.Value)
        {
            LogMessage("🛑 Mouse movement blocked: Plugin is disabled");
            return;
        }

        try
        {
            var purchaseWindow = GameController.IngameState.IngameUi.PurchaseWindowHideout;
            if (!purchaseWindow.IsVisible)
            {
                LogMessage("MoveMouseToItemLocation: Purchase window is not visible");
                return;
            }

            var stashPanel = purchaseWindow.TabContainer.StashInventoryPanel;
            if (stashPanel == null)
            {
                LogMessage("MoveMouseToItemLocation: Stash panel is null");
                return;
            }

            var rect = stashPanel.GetClientRectCache;
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                LogMessage("MoveMouseToItemLocation: Invalid stash panel dimensions");
                return;
            }

            float cellWidth = rect.Width / 12.0f;
            float cellHeight = rect.Height / 12.0f;
            var topLeft = rect.TopLeft;
            
            int itemX = (int)(topLeft.X + (x * cellWidth) + (cellWidth * 7 / 8));
            int itemY = (int)(topLeft.Y + (y * cellHeight) + (cellHeight * 7 / 8));
            
            var windowRect = GameController.Window.GetWindowRectangle();
            System.Drawing.Point windowPos = new System.Drawing.Point((int)windowRect.X, (int)windowRect.Y);
            
            int finalX = windowPos.X + itemX;
            int finalY = windowPos.Y + itemY;
            
            LogDebug($"🎯 CALCULATION DEBUG: Input coords=({x},{y}), Panel=({rect.X},{rect.Y},{rect.Width},{rect.Height}), CellSize=({cellWidth},{cellHeight}), TopLeft=({topLeft.X},{topLeft.Y}), ItemPos=({itemX},{itemY}), Window=({windowPos.X},{windowPos.Y}), Final=({finalX},{finalY})");
            
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point(finalX, finalY);
            
            LogDebug($"Moved mouse to item location: Stash({x},{y}) -> Screen({finalX},{finalY}) - Panel size: {rect.Width}x{rect.Height}");
            
            if (_forceAutoBuy)
            {
                await Task.Delay(500);
            }
            else
            {
                await Task.Delay(200);
            }
            
            if (!_forceAutoBuy && !Settings.LiveSearch.AutoFeatures.AutoBuy.Value)
            {
                LogMessage("🚫 AUTO BUY SKIPPED: Auto-buy is disabled in settings");
                return;
            }

            string sigBefore = _forceAutoBuy ? GetInventorySignature() : null;

            RecentItem itemBeingProcessed = null;
            const int maxFindAttempts = 3;
            for (int attempt = 1; attempt <= maxFindAttempts && itemBeingProcessed == null; attempt++)
            {
                itemBeingProcessed = FindRecentItemByCoordinates(x, y);
                if (itemBeingProcessed == null && _teleportedItemInfo != null)
                {
                    itemBeingProcessed = _teleportedItemInfo;
                }
                if (itemBeingProcessed == null)
                {
                    LogMessage($"⏳ ITEM NOT FOUND (attempt {attempt}/{maxFindAttempts}) at ({x},{y}), retrying in 100ms...");
                    await Task.Delay(100);
                }
            }

            if (itemBeingProcessed != null)
            {
                LogMessage($"✅ FOUND ITEM FOR LOG UPDATE: '{itemBeingProcessed.Name}' (Search: {itemBeingProcessed.SearchId}) at ({x}, {y})");
                LogMessage("🔄 CALLING UPDATE AUTO-BUY ATTEMPT...");
                UpdateAutoBuyAttempt(itemBeingProcessed.Name, itemBeingProcessed.SearchId);
                LogMessage("✅ UPDATE AUTO-BUY ATTEMPT COMPLETED");
            }
            else
            {
                LogMessage($"❌ ITEM NOT FOUND AFTER RETRIES for coordinates ({x}, {y}) - skipping auto-buy click");
                LogMessage("💡 Checked both recent items and teleported item info");
                return;
            }

            LogMessage("⏳ AUTO BUY DELAY: Waiting 100ms before click...");
            await Task.Delay(100);

            bool shouldVerify = false;
            bool isFastMode = false;
            
            if (_forceAutoBuy)
            {
                shouldVerify = true;
            }
            else if (itemBeingProcessed != null && !string.IsNullOrEmpty(itemBeingProcessed.SearchId))
            {
                var searchConfig = GetSearchConfigBySearchId(itemBeingProcessed.SearchId);
                if (searchConfig != null)
                {
                    isFastMode = searchConfig.FastMode.Value;
                    shouldVerify = !isFastMode && Settings.LiveSearch.AutoFeatures.AutoBuy.Value;
                }
            }

            if (skipClick)
            {
                // Just move mouse, don't click - caller will handle verification and clicking
                return;
            }

            if (shouldVerify && itemBeingProcessed != null)
            {
                bool verified = await VerifyItemFromClipboardAsync(itemBeingProcessed.Name, itemBeingProcessed.Price, maxRetries: 3);
                if (!verified)
                {
                    LogMessage($"⚠️ VERIFICATION FAILED: Item at ({x},{y}) verification failed after retries. Proceeding with click anyway (may be tooltip delay).");
                }
            }

            LogMessage("🖱️ AUTO BUY CLICK: Performing Ctrl+Left Click");
            await PerformCtrlLeftClickAsync();
            LogMessage("✅ AUTO BUY COMPLETE: Ctrl+Left Click performed");

            // Note: Second click logic is now handled in TryBuyItemWithRetriesAsync
            // This path is only used for LiveSearch, not BulkBuy
            if (_forceAutoBuy && !string.IsNullOrEmpty(sigBefore))
            {
                // For BulkBuy, the retry logic in TryBuyItemWithRetriesAsync handles everything
                // This code path should not be reached for BulkBuy items
                LogDebug("MoveMouseToItemLocation: Skipping second click logic (handled by TryBuyItemWithRetriesAsync)");
            }

        }
        catch (Exception ex)
        {
            LogError($"MoveMouseToItemLocation failed: {ex.Message}");
        }
    }

    private void TeleportToSpecificItem(RecentItem item)
    {
        try
        {
            if (!Settings.Enable.Value)
            {
                LogMessage("🛑 Teleport blocked: Plugin is disabled");
                return;
            }

            if (!this.GameController.Area.CurrentArea.IsHideout)
            {
                LogMessage("Teleport skipped: Not in hideout zone.");
                return;
            }

            LogDebug("🎯 GUI TELEPORT: User clicked teleport button");

            LogMessage($"🎯 SPECIFIC ITEM TP: Teleporting to {item.Name} at ({item.X}, {item.Y})");

            lock (_recentItemsLock)
            {
                var tempQueue = new Queue<RecentItem>();
                tempQueue.Enqueue(item);
                
                foreach (var otherItem in _recentItems)
                {
                    if (otherItem != item)
                    {
                        tempQueue.Enqueue(otherItem);
                    }
                }
                
                _recentItems = tempQueue;
            }
            
            TravelToHideout(isManual: true);
        }
        catch (Exception ex)
        {
            LogError($"TeleportToSpecificItem failed: {ex.Message}");
        }
    }

    private void RemoveSpecificItem(RecentItem itemToRemove)
    {
        try
        {
            LogMessage($"🗑️ REMOVING ITEM: {itemToRemove.Name} from recent items list");
            
            lock (_recentItemsLock)
            {
                var tempQueue = new Queue<RecentItem>();
                
                foreach (var item in _recentItems)
                {
                    if (item != itemToRemove)
                    {
                        tempQueue.Enqueue(item);
                    }
                }
                
                _recentItems = tempQueue;
                LogMessage($"📦 Item removed. {_recentItems.Count} items remaining.");
            }
        }
        catch (Exception ex)
        {
            LogError($"RemoveSpecificItem failed: {ex.Message}");
        }
    }



    public void PlaySoundWithNAudio(string soundPath, Action<string> logMessage, Action<string> logError)
    {
        Task.Run(async () =>
        {
            if (_isDisposed || _audioDisposalToken == null || _audioSemaphore == null || _audioDisposalToken.IsCancellationRequested)
            {
                logMessage("Audio playback skipped - plugin is shutting down or not initialized");
                return;
            }

            bool acquired = false;
            try
            {
                acquired = await _audioSemaphore.WaitAsync(0, _audioDisposalToken.Token);
                
                if (!acquired)
                {
                    logMessage("Audio playback skipped - another sound is currently playing");
                    return;
                }

                if (_isDisposed || _audioDisposalToken.IsCancellationRequested)
                {
                    return;
                }

                using (var audioFile = new AudioFileReader(soundPath))
                using (var outputDevice = new WaveOutEvent())
                {
                    outputDevice.Init(audioFile);
                    outputDevice.Play();
                    
                    while (outputDevice.PlaybackState == PlaybackState.Playing)
                    {
                        if (_audioDisposalToken.IsCancellationRequested)
                        {
                            outputDevice.Stop();
                            logMessage("Audio playback cancelled - plugin is shutting down");
                            return;
                        }
                        
                        await Task.Delay(10, _audioDisposalToken.Token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                logMessage("Audio playback cancelled gracefully");
            }
            catch (ObjectDisposedException)
            {
                logMessage("Audio resources disposed during playback");
            }
            catch (Exception ex)
            {
                if (_isDisposed || _audioDisposalToken.IsCancellationRequested)
                {
                    return;
                }

                logError($"NAudio playback failed: {ex.Message}");
                
                try
                {
                    logMessage("Attempting fallback to System.Media.SoundPlayer...");
                    using (var player = new System.Media.SoundPlayer(soundPath))
                    {
                        player.Play();
                    }
                    logMessage("Fallback audio playback successful");
                }
                catch (Exception fallbackEx)
                {
                    logError($"Fallback audio playback also failed: {fallbackEx.Message}");
                }
            }
            finally
            {
                if (acquired)
                {
                    try
                    {
                        _audioSemaphore?.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }, _audioDisposalToken.Token);
    }

    private RecentItem FindRecentItemByCoordinates(int x, int y)
    {
        lock (_recentItemsLock)
        {
            LogMessage($"🔍 SEARCHING FOR ITEM: Looking for coordinates ({x}, {y}) in {_recentItems.Count} recent items");

            foreach (var item in _recentItems)
            {
                LogMessage($"📋 CHECKING ITEM: '{item.Name}' at ({item.X}, {item.Y}) - SearchId: {item.SearchId}");
                if (item.X == x && item.Y == y)
                {
                    LogMessage($"✅ FOUND MATCHING ITEM: '{item.Name}' at ({x}, {y})");
                    return item;
                }
            }

            LogMessage($"❌ NO ITEM FOUND for coordinates ({x}, {y})");
        }
        return null;
    }

    private async Task<bool> VerifyItemFromClipboardAsync(string expectedItemName, string expectedPrice, int maxRetries = 1)
    {
        for (int retry = 1; retry <= maxRetries; retry++)
        {
            try
            {
                bool ctrlWasHeld = _bulkBuyCtrlHeld;
                
                // If Ctrl is held, release it before sending Ctrl+C
                if (ctrlWasHeld)
                {
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    await Task.Delay(50);
                }
                
                // Send Ctrl+C
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                await Task.Delay(30);
                keybd_event(0x43, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero); // C key
                await Task.Delay(30);
                keybd_event(0x43, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                await Task.Delay(30);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                await Task.Delay(100);
                
                // Re-press Ctrl if it was being held
                if (ctrlWasHeld)
                {
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                    await Task.Delay(50);
                }

                // Wait 200ms for clipboard to populate
                await Task.Delay(200);

                string clipboardText = "";
                try
                {
                    clipboardText = System.Windows.Forms.Clipboard.GetText();
                }
                catch (Exception ex)
                {
                    LogMessage($"📋 BulkBuy: Failed to read clipboard: {ex.Message}");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(clipboardText))
                {
                    LogMessage($"📋 BulkBuy: Clipboard is empty (attempt {retry}/{maxRetries})");
                    if (retry < maxRetries)
                    {
                        await Task.Delay(200);
                        continue;
                    }
                    return false;
                }

                // Log clipboard preview
                string preview = clipboardText.Length > 300 ? clipboardText.Substring(0, 300) + "..." : clipboardText;
                string sanitized = preview.Replace("\r", "").Replace("\n", " | ");
                LogMessage($"📋 BulkBuy: Clipboard ({clipboardText.Length} chars): {sanitized}");

                if (string.IsNullOrWhiteSpace(clipboardText))
                {
                    if (retry < maxRetries)
                    {
                        LogDebug($"BulkBuy: Clipboard is empty (attempt {retry}/{maxRetries}), retrying...");
                        await Task.Delay(200);
                        continue;
                    }
                    LogMessage("BulkBuy: Clipboard is empty after all retries, verification failed.");
                    return false;
                }

            LogDebug($"BulkBuy: Clipboard text length: {clipboardText.Length}");

            if (!string.IsNullOrWhiteSpace(expectedItemName))
            {
                string expectedNameLower = expectedItemName.ToLowerInvariant();
                string clipboardLower = clipboardText.ToLowerInvariant();
                
                if (!clipboardLower.Contains(expectedNameLower))
                {
                    LogMessage($"BulkBuy: Item name mismatch. Expected: '{expectedItemName}', Clipboard contains: '{clipboardText.Substring(0, Math.Min(100, clipboardText.Length))}...'");
                    return false;
                }
                LogDebug($"BulkBuy: Item name verified: '{expectedItemName}' found in clipboard");
            }

            if (!string.IsNullOrWhiteSpace(expectedPrice))
            {
                string expectedPriceLower = expectedPrice.ToLowerInvariant().Trim();
                
                string[] lines = clipboardText.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                string noteLine = null;
                foreach (var line in lines)
                {
                    if (line.Trim().StartsWith("Note:", StringComparison.OrdinalIgnoreCase))
                    {
                        noteLine = line.Trim();
                        break;
                    }
                }

                if (noteLine == null)
                {
                    LogMessage("BulkBuy: No 'Note:' line found in clipboard, cannot verify price.");
                    return false;
                }

                string noteContent = noteLine.Substring("Note:".Length).Trim();
                
                if (noteContent.StartsWith("~"))
                {
                    int spaceIndex = noteContent.IndexOf(' ');
                    if (spaceIndex > 0)
                    {
                        noteContent = noteContent.Substring(spaceIndex).Trim();
                    }
                }

                string noteContentLower = noteContent.ToLowerInvariant();
                
                bool priceMatches = noteContentLower == expectedPriceLower || 
                                   noteContentLower.Contains(expectedPriceLower) ||
                                   expectedPriceLower.Contains(noteContentLower);

                if (!priceMatches)
                {
                    LogMessage($"BulkBuy: Price mismatch. Expected: '{expectedPrice}', Found in Note: '{noteContent}'");
                    return false;
                }

                LogDebug($"BulkBuy: Price verified: '{expectedPrice}' matches Note: '{noteContent}'");
            }

                LogMessage("BulkBuy: Item verification passed (name and price match)");
                return true;
            }
            catch (Exception ex)
            {
                if (retry < maxRetries)
                {
                    LogDebug($"BulkBuy: Error verifying item (attempt {retry}/{maxRetries}): {ex.Message}, retrying...");
                    await Task.Delay(200);
                    continue;
                }
                LogError($"BulkBuy: Error verifying item from clipboard after {maxRetries} attempts: {ex.Message}");
                return false;
            }
        }

        return false;
    }
}

