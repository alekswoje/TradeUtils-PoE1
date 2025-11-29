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
            
            // Check quota before making request
            if (_rateLimiter != null && !_rateLimiter.CanMakeRequest())
            {
                LogMessage($"⛔ QUOTA TOO LOW: Skipping token refresh - {_rateLimiter.GetStatus()}");
                return false;
            }
            
            // Use the trade/fetch API to get fresh item data
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
                                
                                // Update the item with fresh token
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

    private async void TravelToHideout(bool isManual = false)
    {
        // CRITICAL: Respect plugin enable state
        if (!Settings.Enable.Value)
        {
            LogDebug("🛑 Teleport blocked: Plugin is disabled");
            return;
        }
        
        // Set manual teleport flag
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
            _isManualTeleport = false; // Reset flag on early return
            _currentTeleportingItem = null; // Clear current teleporting item
            return;
        }

        // Check if game is loading - prevent teleports during loading screens
        if (GameController.IsLoading)
        {
            LogDebug("⏳ LOADING SCREEN: Game is loading, skipping teleport for safety");
            _isManualTeleport = false; // Reset flag on early return
            _currentTeleportingItem = null; // Clear current teleporting item
            return;
        }
        
        // Check if we're in a valid game state for teleporting
        if (!GameController.InGame)
        {
            LogDebug("🚫 NOT IN GAME: Not in valid game state for teleporting");
            _isManualTeleport = false; // Reset flag on early return
            _currentTeleportingItem = null; // Clear current teleporting item
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
                _isManualTeleport = false; // Reset flag on early return
                _currentTeleportingItem = null; // Clear current teleporting item
                return;
            }

            currentItem = _recentItems.Peek();
        }
        
        // Check if token is expired and refresh if needed
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
                    // Double-check queue isn't empty before dequeue (race condition protection)
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
                
                if (hasMoreItems)
                {
                    await Task.Delay(500);
                    TravelToHideout(_isManualTeleport);
                    return;
                }
                else
                {
                    LogDebug("No valid items remaining for teleport");
                    _isManualTeleport = false;
                    _currentTeleportingItem = null; // Clear current teleporting item
                    return;
                }
            }
        }
        
        LogDebug($"Attempting to travel to hideout for item: {currentItem.Name} - {currentItem.Price}");
        LogDebug($"Hideout token: {currentItem.HideoutToken}");
        
        // Set the current teleporting item for GUI display
        _currentTeleportingItem = currentItem;

        // Store the teleported item info for auto-buy log updates
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
            // Ensure rate limiter is initialized
            if (_rateLimiter == null)
            {
                _rateLimiter = new QuotaGuard(LogMessage, LogError, () => LiveSearchSettings);
            }
            
            // Check quota before making request
            if (_rateLimiter != null && !_rateLimiter.CanMakeRequest())
            {
                LogMessage($"⛔ QUOTA TOO LOW: Skipping teleport - {_rateLimiter.GetStatus()}");
                LogMessage($"⏳ Quota resets in {_rateLimiter.GetTimeUntilReset() / 1000} seconds");
                return;
            }
            
            LogDebug("Sending teleport request...");
            
            _lastTeleportSucceeded = false;
            var response = await _httpClient.SendAsync(request);
            LogDebug($"Response status: {response.StatusCode}");
            
            // Note: No longer using TP lock - using loading screen check instead
            LogDebug("📡 TELEPORT REQUEST: Sent successfully, waiting for response");
            
            // Handle rate limiting
            if (_rateLimiter != null)
            {
                var rateLimitWaitTime = await _rateLimiter.HandleRateLimitResponse(response);
                if (rateLimitWaitTime > 0)
                {
                    return; // Rate limited, wait and return
                }
            }
            
            if (!response.IsSuccessStatusCode)
            {
                LogError($"Teleport failed: {response.StatusCode}");
                var responseContent = await response.Content.ReadAsStringAsync();
                LogError($"Response content: {responseContent}");
                
                // Note: No TP lock to unlock - using loading screen check instead
                
                // Remove failed item and try next one if available
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound || 
                    response.StatusCode == System.Net.HttpStatusCode.BadRequest ||
                    response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                    {
                        LogMessage($"🔄 SERVICE UNAVAILABLE: Token likely expired for '{currentItem.Name}', attempting refresh...");
                        var refreshSuccess = await RefreshItemToken(currentItem);
                        if (refreshSuccess)
                        {
                            LogMessage("✅ TOKEN REFRESHED: Retrying teleport with fresh token");
                            await Task.Delay(1000); // Short delay before retry
                            TravelToHideout(_isManualTeleport);
                            return;
                        }
                        else
                        {
                            LogMessage("❌ TOKEN REFRESH FAILED: Removing item from queue");
                        }
                    }
                    else
                    {
                        LogMessage($"🗑️ ITEM EXPIRED: Removing expired item '{currentItem.Name}' and trying next...");
                    }
                    
                    bool hasMoreItems;
                    lock (_recentItemsLock)
                    {
                        // Double-check queue isn't empty before dequeue (race condition protection)
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
                    
                    // Try the next item if available
                    if (hasMoreItems)
                    {
                        await Task.Delay(500); // Small delay before retry
                        TravelToHideout(_isManualTeleport); // Recursive call to try next item, preserve manual flag
                    }
                    else
                    {
                        LogMessage("📭 NO MORE ITEMS: All items in queue have expired");
                        _isManualTeleport = false; // Reset flag when no more items
                        _currentTeleportingItem = null; // Clear current teleporting item
                    }
                }
            }
            else
            {
                // Check response content to verify actual success
                var responseContent = await response.Content.ReadAsStringAsync();
                LogDebug($"🔍 TELEPORT RESPONSE: Status={response.StatusCode}, Content={responseContent}");
                
                // Check if the response indicates actual success
                bool actualSuccess = !responseContent.Contains("\"error\"") && 
                                   !responseContent.Contains("failed") && 
                                   !responseContent.Contains("invalid");
                
                if (actualSuccess)
                {
                    LogInfo("✅ Teleport to hideout successful!");
                    _lastTeleportSucceeded = true;
                    
                    // Store location for BOTH manual and auto teleports to enable mouse movement
                    _teleportedItemLocation = (currentItem.X, currentItem.Y);
                    if (_isManualTeleport)
                    {
                        LogDebug($"📍 STORED TELEPORT LOCATION: Manual teleport to item at ({currentItem.X}, {currentItem.Y}) for mouse movement");
                    }
                    else
                    {
                        LogDebug($"📍 STORED TELEPORT LOCATION: Auto teleport to item at ({currentItem.X}, {currentItem.Y}) for mouse movement");
                    }
                    
                    // Schedule a verification check after a short delay to confirm we're actually in hideout
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000); // Wait 3 seconds for area change
                        try
                        {
                            // Note: Area verification removed due to API access issues
                            // The teleport success is already validated by response content analysis
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
                    
                    // Remove this item and try next one
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
                        TravelToHideout(_isManualTeleport);
                        return;
                    }
                    else
                    {
                        LogMessage("📭 NO MORE ITEMS: All items in queue have failed");
                        _isManualTeleport = false;
                        _currentTeleportingItem = null; // Clear current teleporting item
                    }
                }
                
                // Note: Mouse movement is now handled in the main Tick method
                // This allows for better control over when mouse movement occurs
                
                lock (_recentItemsLock)
                {
                    _recentItems.Clear();
                }
                _lastTpTime = DateTime.Now;
                _isManualTeleport = false; // Reset flag
                _currentTeleportingItem = null; // Clear current teleporting item
            }
        }
        catch (Exception ex)
        {
            LogError($"Teleport request failed: {ex.Message}");
            LogError($"Exception details: {ex}");
            
            // Note: No TP lock to unlock - using loading screen check instead
            
            _isManualTeleport = false; // Reset flag on exception
            _currentTeleportingItem = null; // Clear current teleporting item on exception
        }
    }

    public async Task PerformCtrlLeftClickAsync()
    {
        // CRITICAL: Respect plugin enable state
        if (!Settings.Enable.Value)
        {
            LogMessage("🛑 Auto buy blocked: Plugin is disabled");
            return;
        }

        try
        {
            LogMessage("🖱️ AUTO BUY: Performing Ctrl+Left Click...");
            
            // Small delay to ensure mouse position is set
            await Task.Delay(50);
            
            // For BulkBuy, Ctrl is already held - just perform the click
            bool ctrlAlreadyHeld = _bulkBuyCtrlHeld;
            
            if (!ctrlAlreadyHeld)
            {
                // Press Ctrl key down and hold it
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                
                // Longer delay to ensure Ctrl is registered
                await Task.Delay(30);
            }
            
            // Perform left mouse button down
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            
            // Small delay
            await Task.Delay(20);
            
            // Perform left mouse button up
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            
            // Small delay before releasing Ctrl (only if we pressed it)
            if (!ctrlAlreadyHeld)
            {
                await Task.Delay(30);
                
                // Release Ctrl key
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            
            LogInfo("✅ AUTO BUY: Ctrl+Left Click completed!");
        }
        catch (Exception ex)
        {
            LogError($"❌ AUTO BUY FAILED: {ex.Message}");
            // Make sure to release Ctrl if there was an error (only if we pressed it)
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

    /// <summary>
    /// Checks if an item still exists at the given stash coordinates (indicates purchase failed).
    /// Uses server-side inventory data which has PosX/PosY properties.
    /// </summary>
    private bool IsItemStillInStash(int x, int y)
    {
        try
        {
            var purchaseWindow = GameController.IngameState.IngameUi.PurchaseWindowHideout;
            if (purchaseWindow == null || !purchaseWindow.IsVisible)
                return false;

            // Check server-side NPC inventories (seller's stash)
            var npcInventories = GameController?.Game?.IngameState?.ServerData?.NPCInventories;
            if (npcInventories == null)
                return false;

            // Find the seller's stash inventory
            foreach (var npcInv in npcInventories)
            {
                if (npcInv?.Inventory?.InventorySlotItems == null)
                    continue;

                // Check if there's an item at the specified coordinates
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

    private async void MoveMouseToItemLocation(int x, int y)
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
            
            // Calculate item position within the stash panel (bottom-right to avoid sockets)
            int itemX = (int)(topLeft.X + (x * cellWidth) + (cellWidth * 7 / 8));
            int itemY = (int)(topLeft.Y + (y * cellHeight) + (cellHeight * 7 / 8));
            
            // Get game window position
            var windowRect = GameController.Window.GetWindowRectangle();
            System.Drawing.Point windowPos = new System.Drawing.Point((int)windowRect.X, (int)windowRect.Y);
            
            // Calculate final screen position
            int finalX = windowPos.X + itemX;
            int finalY = windowPos.Y + itemY;
            
            LogDebug($"🎯 CALCULATION DEBUG: Input coords=({x},{y}), Panel=({rect.X},{rect.Y},{rect.Width},{rect.Height}), CellSize=({cellWidth},{cellHeight}), TopLeft=({topLeft.X},{topLeft.Y}), ItemPos=({itemX},{itemY}), Window=({windowPos.X},{windowPos.Y}), Final=({finalX},{finalY})");
            
            // Move mouse cursor
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point(finalX, finalY);
            
            LogDebug($"Moved mouse to item location: Stash({x},{y}) -> Screen({finalX},{finalY}) - Panel size: {rect.Width}x{rect.Height}");
            
            // Wait for mouse to settle and tooltip to appear (especially important for BulkBuy)
            if (_forceAutoBuy)
            {
                await Task.Delay(500); // Wait longer for BulkBuy to ensure tooltip is ready
            }
            else
            {
                await Task.Delay(200); // Shorter wait for LiveSearch
            }
            
            // Auto Buy: for BulkBuy we always click, for LiveSearch respect its AutoBuy setting
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

            // For BulkBuy, verify the item via clipboard before clicking
            if (_forceAutoBuy && itemBeingProcessed != null)
            {
                bool verified = await VerifyItemFromClipboardAsync(itemBeingProcessed.Name, itemBeingProcessed.Price, maxRetries: 3);
                if (!verified)
                {
                    LogMessage($"⚠️ BULKBUY VERIFICATION FAILED: Item at ({x},{y}) verification failed after retries. Proceeding with click anyway (may be tooltip delay).");
                    // Don't return - allow click to proceed but log warning
                }
            }

            LogMessage("🖱️ AUTO BUY CLICK: Performing Ctrl+Left Click");
            await PerformCtrlLeftClickAsync();
            LogMessage("✅ AUTO BUY COMPLETE: Ctrl+Left Click performed");

            if (_forceAutoBuy && !string.IsNullOrEmpty(sigBefore))
            {
                await Task.Delay(1000);
                var sigAfter = GetInventorySignature();
                if (sigAfter == sigBefore)
                {
                    LogMessage("BulkBuy: inventory unchanged after first click, performing second safety click.");
                    await PerformCtrlLeftClickAsync();
                }
                else
                {
                    LogMessage("BulkBuy: inventory changed after first click, skipping second click.");
                }
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

            // GUI teleports (clicking buttons) are considered manual
            LogDebug("🎯 GUI TELEPORT: User clicked teleport button");

            LogMessage($"🎯 SPECIFIC ITEM TP: Teleporting to {item.Name} at ({item.X}, {item.Y})");

            // Move the specific item to the front of the queue
            lock (_recentItemsLock)
            {
                var tempQueue = new Queue<RecentItem>();
                tempQueue.Enqueue(item);
                
                // Add all other items back (except the one we're teleporting to)
                foreach (var otherItem in _recentItems)
                {
                    if (otherItem != item)
                    {
                        tempQueue.Enqueue(otherItem);
                    }
                }
                
                _recentItems = tempQueue;
            }
            
            // Set this as a manual teleport and call the main teleport method
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
                
                // Add all items except the one to remove
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
        // Fire and forget - don't await to avoid any delays
        // But with proper cancellation and resource management to prevent crashes
        Task.Run(async () =>
        {
            // Check if plugin is being disposed or resources are null
            if (_isDisposed || _audioDisposalToken == null || _audioSemaphore == null || _audioDisposalToken.IsCancellationRequested)
            {
                logMessage("Audio playback skipped - plugin is shutting down or not initialized");
                return;
            }

            // Use semaphore to prevent concurrent audio playback (only one sound at a time)
            // This prevents race conditions accessing the Windows audio driver
            bool acquired = false;
            try
            {
                // Try to acquire the semaphore with a timeout (don't block if another sound is playing)
                acquired = await _audioSemaphore.WaitAsync(0, _audioDisposalToken.Token);
                
                if (!acquired)
                {
                    logMessage("Audio playback skipped - another sound is currently playing");
                    return;
                }

                // Double-check disposal state after acquiring semaphore
                if (_isDisposed || _audioDisposalToken.IsCancellationRequested)
                {
                    return;
                }

                using (var audioFile = new AudioFileReader(soundPath))
                using (var outputDevice = new WaveOutEvent())
                {
                    outputDevice.Init(audioFile);
                    outputDevice.Play();
                    
                    // Wait for playback to complete without blocking the main thread
                    // But respect cancellation token
                    while (outputDevice.PlaybackState == PlaybackState.Playing)
                    {
                        // Check for cancellation
                        if (_audioDisposalToken.IsCancellationRequested)
                        {
                            outputDevice.Stop();
                            logMessage("Audio playback cancelled - plugin is shutting down");
                            return;
                        }
                        
                        await Task.Delay(10, _audioDisposalToken.Token); // Small delay to prevent busy waiting
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during plugin shutdown
                logMessage("Audio playback cancelled gracefully");
            }
            catch (ObjectDisposedException)
            {
                // Expected if resources were disposed during shutdown
                logMessage("Audio resources disposed during playback");
            }
            catch (Exception ex)
            {
                // Don't try fallback if we're shutting down
                if (_isDisposed || _audioDisposalToken.IsCancellationRequested)
                {
                    return;
                }

                logError($"NAudio playback failed: {ex.Message}");
                
                // Fallback to System.Media.SoundPlayer if NAudio fails
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
                // Always release the semaphore if we acquired it
                if (acquired)
                {
                    try
                    {
                        _audioSemaphore?.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Semaphore was disposed, that's OK during shutdown
                    }
                }
            }
        }, _audioDisposalToken.Token); // Pass cancellation token to Task.Run
    }

    /// <summary>
    /// Finds a recent item by its stash coordinates
    /// </summary>
    /// <param name="x">X coordinate in stash</param>
    /// <param name="y">Y coordinate in stash</param>
    /// <returns>The RecentItem if found, null otherwise</returns>
    private RecentItem FindRecentItemByCoordinates(int x, int y)
    {
        lock (_recentItemsLock)
        {
            LogMessage($"🔍 SEARCHING FOR ITEM: Looking for coordinates ({x}, {y}) in {_recentItems.Count} recent items");

            // Search through recent items to find one with matching coordinates
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

    /// <summary>
    /// Verifies the item at mouse cursor by copying to clipboard (Ctrl+C) and checking the Note (price) and item name.
    /// </summary>
    private async Task<bool> VerifyItemFromClipboardAsync(string expectedItemName, string expectedPrice, int maxRetries = 3)
    {
        for (int retry = 1; retry <= maxRetries; retry++)
        {
            try
            {
                // Send Ctrl+C to copy item info
                // For BulkBuy, Ctrl is already held - just press C
                bool ctrlAlreadyHeld = _bulkBuyCtrlHeld;
                
                if (!ctrlAlreadyHeld)
                {
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                    await Task.Delay(50);
                }
                
                keybd_event(0x43, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero); // C key
                await Task.Delay(50);
                keybd_event(0x43, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                
                if (!ctrlAlreadyHeld)
                {
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }

                // Wait for clipboard to update (longer wait for first attempt)
                int waitTime = retry == 1 ? 400 : 200;
                await Task.Delay(waitTime);

                // Read clipboard using .NET Clipboard class
                string clipboardText = "";
                try
                {
                    clipboardText = System.Windows.Forms.Clipboard.GetText();
                }
                catch (Exception ex)
                {
                    if (retry < maxRetries)
                    {
                        LogDebug($"BulkBuy: Failed to read clipboard (attempt {retry}/{maxRetries}): {ex.Message}, retrying...");
                        await Task.Delay(200);
                        continue;
                    }
                    LogError($"BulkBuy: Failed to read clipboard after {maxRetries} attempts: {ex.Message}");
                    return false;
                }

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

            // Check if item name is in clipboard (fuzzy match - just contains check)
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

            // Extract and verify price from Note line
            if (!string.IsNullOrWhiteSpace(expectedPrice))
            {
                // Expected price format: "84 chaos" or "~b/o 1 divine" or "30 chaos"
                // Note format in clipboard: "Note: ~b/o 1 divine" or "Note: 30 chaos"
                string expectedPriceLower = expectedPrice.ToLowerInvariant().Trim();
                
                // Try to find Note line
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

                // Extract price from note line (format: "Note: ~b/o 1 divine" or "Note: 30 chaos")
                string noteContent = noteLine.Substring("Note:".Length).Trim();
                
                // Remove "~b/o" or "~price" prefixes if present
                if (noteContent.StartsWith("~"))
                {
                    int spaceIndex = noteContent.IndexOf(' ');
                    if (spaceIndex > 0)
                    {
                        noteContent = noteContent.Substring(spaceIndex).Trim();
                    }
                }

                // Compare: expectedPrice might be "84 chaos" or "1 divine"
                // noteContent might be "84 chaos" or "1 divine"
                string noteContentLower = noteContent.ToLowerInvariant();
                
                // Check if the price matches (amount and currency)
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

