using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;

namespace TradeUtils;

public partial class TradeUtils
{
    private CancellationTokenSource _autoStashCancellationToken;

    /// <summary>
    /// Starts the auto-stash process (pauses BulkBuy and LiveSearch)
    /// </summary>
    private async Task StartAutoStashAsync()
    {
        if (_autoStashInProgress)
        {
            LogMessage("Auto-stash already in progress");
            return;
        }

        _autoStashInProgress = true;
        _autoStashStartTime = DateTime.Now;
        _autoStashCancellationToken = new CancellationTokenSource();

        // Release Ctrl key if held (from BulkBuy)
        if (_bulkBuyCtrlHeld)
        {
            try
            {
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                _bulkBuyCtrlHeld = false;
                LogMessage("📦 Auto-stash: Released Ctrl key");
            }
            catch (Exception ex)
            {
                LogError($"📦 Auto-stash: Error releasing Ctrl key: {ex.Message}");
            }
        }

        // Pause BulkBuy if running
        if (_bulkBuyInProgress)
        {
            _bulkBuyPausedForStash = true;
            LogMessage("📦 Auto-stash: Pausing BulkBuy...");
        }

        // Pause LiveSearch if running (check if listeners exist and are running)
        if (_liveSearchStarted && _listeners != null && _listeners.Any(l => l.IsRunning))
        {
            _liveSearchPausedForStash = true;
            _liveSearchPaused = true;
            LogMessage("📦 Auto-stash: Pausing LiveSearch...");
        }

        try
        {
            LogMessage("📦 Auto-stash: Starting...");
            await AutoStashAsync(_autoStashCancellationToken.Token);
            LogMessage("✅ Auto-stash: Completed successfully");
        }
        catch (Exception ex)
        {
            LogError($"❌ Auto-stash: Error - {ex.Message}");
        }
        finally
        {
            _autoStashInProgress = false;
            
            // Resume BulkBuy if it was paused
            if (_bulkBuyPausedForStash)
            {
                _bulkBuyPausedForStash = false;
                LogMessage("📦 Auto-stash: Resuming BulkBuy...");
            }

            // Resume LiveSearch if it was paused
            if (_liveSearchPausedForStash)
            {
                _liveSearchPausedForStash = false;
                _liveSearchPaused = false;
                LogMessage("📦 Auto-stash: Resuming LiveSearch...");
            }

            _autoStashCancellationToken?.Dispose();
            _autoStashCancellationToken = null;
        }
    }

    /// <summary>
    /// Main auto-stash logic: goes to hideout, opens stash, and stashes all items
    /// </summary>
    private async Task AutoStashAsync(CancellationToken ct)
    {
        // Wait 5 seconds for user to tab back into game
        LogMessage("📦 Auto-stash: Starting in 5 seconds... (tab back into game)");
        for (int i = 5; i > 0; i--)
        {
            if (ct.IsCancellationRequested)
                return;
            LogMessage($"📦 Auto-stash: Starting in {i} seconds...");
            await Task.Delay(1000, ct);
        }
        LogMessage("📦 Auto-stash: Starting now!");

        // Step 1: Type /hideout command
        LogMessage("📦 Auto-stash: Typing /hideout command...");
        await TypeHideoutCommandAsync(ct);

        // Step 2: Wait for hideout to load
        LogMessage("📦 Auto-stash: Waiting for hideout to load...");
        await WaitForHideoutLoadAsync(ct);

        // Step 2.5: Wait 1-2 seconds after hideout loads for everything to settle
        LogMessage("📦 Auto-stash: Waiting 2 seconds for hideout to fully load...");
        await Task.Delay(2000, ct);

        // Step 2.6: Wait for entities to load after hideout loads
        LogMessage("📦 Auto-stash: Waiting for entities to load...");
        await WaitForEntitiesToLoadAsync(ct);

        // Step 3: Find and click on stash (retry 2-3 times with delays)
        bool stashClicked = false;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            LogMessage($"📦 Auto-stash: Looking for stash (attempt {attempt}/3)...");
            stashClicked = await FindAndClickStashAsync(ct);
            
            if (stashClicked)
            {
                LogMessage($"📦 Auto-stash: Successfully found and clicked stash on attempt {attempt}");
                break;
            }
            
            if (attempt < 3)
            {
                LogMessage($"📦 Auto-stash: Attempt {attempt} failed, waiting 500ms before retry...");
                await Task.Delay(500, ct);
            }
        }
        
        if (!stashClicked)
        {
            LogError("📦 Auto-stash: Failed to find or click stash after 3 attempts");
            return;
        }

        // Step 4: Wait for stash to open
        LogMessage("📦 Auto-stash: Waiting for stash to open...");
        var stashOpened = await WaitForStashToOpenAsync(ct);
        if (!stashOpened)
        {
            LogError("📦 Auto-stash: Stash did not open");
            return;
        }

        // Step 5: Stash all inventory items
        LogMessage("📦 Auto-stash: Stashing inventory items...");
        await StashAllInventoryItemsAsync(ct);
    }

    /// <summary>
    /// Types /hideout command in chat
    /// </summary>
    private async Task TypeHideoutCommandAsync(CancellationToken ct)
    {
        // Press Enter to open chat
        keybd_event(0x0D, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero); // VK_RETURN
        await Task.Delay(100, ct);
        keybd_event(0x0D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        await Task.Delay(200, ct);

        // Type /hideout
        string command = "/hideout";
        foreach (char c in command)
        {
            byte vk = GetVirtualKeyCode(c);
            if (vk != 0)
            {
                keybd_event(vk, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                await Task.Delay(50, ct);
                keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                await Task.Delay(50, ct);
            }
        }

        // Press Enter to execute command
        await Task.Delay(100, ct);
        keybd_event(0x0D, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
        await Task.Delay(100, ct);
        keybd_event(0x0D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        await Task.Delay(200, ct);
    }

    /// <summary>
    /// Gets virtual key code for a character
    /// </summary>
    private byte GetVirtualKeyCode(char c)
    {
        if (c >= 'a' && c <= 'z')
            return (byte)(c - 'a' + 0x41); // A-Z keys
        if (c >= 'A' && c <= 'Z')
            return (byte)(c - 'A' + 0x41);
        if (c >= '0' && c <= '9')
            return (byte)(c - '0' + 0x30);
        if (c == '/')
            return 0xBF; // VK_OEM_2
        return 0;
    }

    /// <summary>
    /// Waits for hideout to load
    /// </summary>
    private async Task WaitForHideoutLoadAsync(CancellationToken ct)
    {
        int maxWait = 10000; // 10 seconds
        int elapsed = 0;
        int checkInterval = 100;

        while (elapsed < maxWait && !ct.IsCancellationRequested)
        {
            if (!GameController.IsLoading)
            {
                var areaName = GameController.Area?.CurrentArea?.DisplayName ?? "";
                if (areaName.Contains("Hideout", StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage("📦 Auto-stash: Hideout loaded");
                    return;
                }
            }
            await Task.Delay(checkInterval, ct);
            elapsed += checkInterval;
        }

        if (elapsed >= maxWait)
        {
            LogWarning("📦 Auto-stash: Timeout waiting for hideout to load");
        }
    }

    /// <summary>
    /// Waits for entities to load after hideout loads
    /// </summary>
    private async Task WaitForEntitiesToLoadAsync(CancellationToken ct)
    {
        int maxWait = 5000; // 5 seconds
        int elapsed = 0;
        int checkInterval = 200;

        while (elapsed < maxWait && !ct.IsCancellationRequested)
        {
            // Check if entities are loaded by trying to access EntityListWrapper
            try
            {
                var entities = GameController?.EntityListWrapper?.ValidEntitiesByType;
                if (entities != null)
                {
                    // Check if we can find stash entities
                    if (entities.ContainsKey(EntityType.Stash))
                    {
                        var stashes = entities[EntityType.Stash];
                        if (stashes != null && stashes.Count > 0)
                        {
                            LogMessage($"📦 Auto-stash: Entities loaded (found {stashes.Count} stash entities)");
                            return;
                        }
                    }
                    
                    // If EntityListWrapper exists but no stashes yet, wait a bit more
                    // This means entities are loading but stash might not be ready
                    await Task.Delay(checkInterval, ct);
                    elapsed += checkInterval;
                    continue;
                }
            }
            catch
            {
                // EntityListWrapper not ready yet, continue waiting
            }

            await Task.Delay(checkInterval, ct);
            elapsed += checkInterval;
        }

        // Even if we timeout, continue - stash might still be findable via fallback method
        if (elapsed >= maxWait)
        {
            LogMessage("📦 Auto-stash: Entities may still be loading, continuing anyway...");
        }
    }

    /// <summary>
    /// Finds and clicks on the stash entity (using BetterFollowbot approach: entities + labels)
    /// Searches both entities and ground item labels multiple times
    /// </summary>
    private async Task<bool> FindAndClickStashAsync(CancellationToken ct)
    {
        try
        {
            // Step 1: Get stash entities (like BetterFollowbot)
            var stashEntities = new List<Entity>();

            try
            {
                var stashes = GameController?.EntityListWrapper?.ValidEntitiesByType[EntityType.Stash];
                if (stashes != null)
                {
                    stashEntities.AddRange(stashes);
                    LogMessage($"📦 Auto-stash: Found {stashes.Count} stash entities from ValidEntitiesByType");
                }
            }
            catch (Exception ex)
            {
                LogDebug($"📦 Auto-stash: ValidEntitiesByType failed: {ex.Message}");
            }

            // Fallback: search all entities
            if (stashEntities.Count == 0)
            {
                var allEntities = GameController?.Entities;
                if (allEntities != null)
                {
                    var directStashes = allEntities.Where(x =>
                        x != null &&
                        x.IsValid &&
                        x.Type == EntityType.Stash)
                        .ToList();
                    stashEntities.AddRange(directStashes);
                    LogMessage($"📦 Auto-stash: Found {directStashes.Count} stash entities from Entities collection");
                }
            }

            // Step 2: Get labels from ItemsOnGroundLabels (like BetterFollowbot)
            var allLabels = GameController?.Game?.IngameState?.IngameUi?.ItemsOnGroundLabels?.ToList();
            var matchedLabels = new List<LabelOnGround>();

            // Match entities to labels by address
            foreach (var entity in stashEntities)
            {
                if (entity == null) continue;

                var matchingLabel = allLabels?.FirstOrDefault(label =>
                    label?.ItemOnGround != null &&
                    label.ItemOnGround.Address == entity.Address);

                if (matchingLabel != null)
                {
                    matchedLabels.Add(matchingLabel);
                    LogMessage($"📦 Auto-stash: Matched stash entity to label '{matchingLabel.Label?.Text}' at distance {entity.DistancePlayer:F1}");
                }
            }

            // Step 3: Also check ItemsOnGroundLabels directly for stash items
            var labelsFromItemsOnGround = allLabels?.Where(x =>
            {
                if (x == null || x.ItemOnGround == null) return false;
                return x.ItemOnGround.Type == EntityType.Stash;
            }).ToList() ?? new List<LabelOnGround>();

            LogMessage($"📦 Auto-stash: Found {labelsFromItemsOnGround.Count} stash labels from ItemsOnGroundLabels");

            // Step 4: Combine results (like BetterFollowbot)
            var combinedLabels = matchedLabels.Union(labelsFromItemsOnGround).ToList();
            LogMessage($"📦 Auto-stash: Combined total: {combinedLabels.Count} unique stash labels (entities: {matchedLabels.Count}, labels: {labelsFromItemsOnGround.Count})");

            if (combinedLabels.Count == 0)
            {
                LogError("📦 Auto-stash: No stash labels found");
                return false;
            }

            // Step 5: Find the closest visible stash
            var visibleStash = combinedLabels
                .Where(x => x.IsVisible && x.Label?.IsVisible == true && x.ItemOnGround != null)
                .OrderBy(x => x.ItemOnGround.DistancePlayer)
                .FirstOrDefault();

            if (visibleStash == null)
            {
                LogError("📦 Auto-stash: No visible stash found");
                return false;
            }

            LogMessage($"📦 Auto-stash: Selected stash '{visibleStash.Label?.Text}' at distance {visibleStash.ItemOnGround.DistancePlayer:F1}");

            // Step 6: Click on the stash entity's world position (like BetterFollowbot)
            try
            {
                var renderComponent = visibleStash.ItemOnGround.GetComponent<ExileCore.PoEMemory.Components.Render>();
                if (renderComponent?.Pos != null)
                {
                    var worldPos = renderComponent.Pos;
                    var camera = GameController.Game.IngameState.Camera;
                    var windowRect = GameController.Window.GetWindowRectangleTimeCache;
                    var screenPos = camera.WorldToScreen(worldPos);
                    var finalPos = screenPos + windowRect.TopLeft;
                    
                    // Clamp to window bounds (like BetterFollowbot Helper)
                    var edgeBounds = 50;
                    if (finalPos.X < windowRect.TopLeft.X) finalPos.X = windowRect.TopLeft.X + edgeBounds;
                    if (finalPos.Y < windowRect.TopLeft.Y) finalPos.Y = windowRect.TopLeft.Y + edgeBounds;
                    if (finalPos.X > windowRect.BottomRight.X) finalPos.X = windowRect.BottomRight.X - edgeBounds;
                    if (finalPos.Y > windowRect.BottomRight.Y) finalPos.Y = windowRect.BottomRight.Y - edgeBounds;
                    
                    var clickPos = new System.Drawing.Point((int)finalPos.X, (int)finalPos.Y);
                    LogMessage($"📦 Auto-stash: Clicking stash at world position ({worldPos.X:F1}, {worldPos.Y:F1}, {worldPos.Z:F1}) -> screen ({clickPos.X}, {clickPos.Y})");
                    
                    System.Windows.Forms.Cursor.Position = clickPos;
                    await Task.Delay(150, ct);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(50, ct);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    await Task.Delay(500, ct); // Wait longer for stash to open
                    
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"📦 Auto-stash: Error getting world position: {ex.Message}, trying label position");
            }

            // Fallback: Click on label position
            var labelPos = visibleStash.Label.GetClientRect().Center;
            var windowPos = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
            var fallbackClickPos = new System.Drawing.Point(
                (int)(labelPos.X + windowPos.X),
                (int)(labelPos.Y + windowPos.Y)
            );

            LogMessage($"📦 Auto-stash: Clicking stash at label position ({fallbackClickPos.X}, {fallbackClickPos.Y})");
            System.Windows.Forms.Cursor.Position = fallbackClickPos;
            await Task.Delay(150, ct);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(50, ct);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(500, ct); // Wait longer for stash to open

            return true;
        }
        catch (Exception ex)
        {
            LogError($"📦 Auto-stash: Error finding/clicking stash: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Waits for stash to open (waits longer and checks after hideout fully loads)
    /// </summary>
    private async Task<bool> WaitForStashToOpenAsync(CancellationToken ct)
    {
        // Wait a bit first to ensure we're fully loaded into hideout
        await Task.Delay(500, ct);
        
        int maxWait = 10000; // 10 seconds (increased from 5)
        int elapsed = 0;
        int checkInterval = 200;

        while (elapsed < maxWait && !ct.IsCancellationRequested)
        {
            // Check if we're still loading
            if (GameController.IsLoading)
            {
                LogDebug("📦 Auto-stash: Still loading, waiting...");
                await Task.Delay(checkInterval, ct);
                elapsed += checkInterval;
                continue;
            }

            // Check if stash is open
            try
            {
                if (GameController.IngameState.IngameUi.StashElement.IsVisible)
                {
                    LogMessage("📦 Auto-stash: Stash is open");
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"📦 Auto-stash: Error checking stash visibility: {ex.Message}");
            }

            await Task.Delay(checkInterval, ct);
            elapsed += checkInterval;
        }

        LogError("📦 Auto-stash: Timeout waiting for stash to open");
        return false;
    }

    /// <summary>
    /// Stashes all inventory items (using HighlightedItems logic)
    /// </summary>
    private async Task StashAllInventoryItemsAsync(CancellationToken ct)
    {
        try
        {
            // Get all inventory items
            var inventoryItems = GameController.IngameState.ServerData.PlayerInventories[0].Inventory.InventorySlotItems
                .OrderBy(x => x.PosX)
                .ThenBy(x => x.PosY)
                .ToList();

            if (inventoryItems.Count == 0)
            {
                LogMessage("📦 Auto-stash: Inventory is empty");
                return;
            }

            LogMessage($"📦 Auto-stash: Found {inventoryItems.Count} items to stash");

            var prevMousePos = System.Windows.Forms.Cursor.Position;

            for (int i = 0; i < inventoryItems.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    LogMessage("📦 Auto-stash: Cancelled");
                    break;
                }

                var item = inventoryItems[i];

                // Check if stash is still open (critical check)
                if (!GameController.IngameState.IngameUi.StashElement.IsVisible)
                {
                    LogMessage("📦 Auto-stash: Stash closed by user, stopping");
                    break;
                }

                // Check if inventory panel is visible
                if (!GameController.IngameState.IngameUi.InventoryPanel.IsVisible)
                {
                    LogMessage("📦 Auto-stash: Inventory panel closed, stopping");
                    break;
                }

                // Get item position and move mouse (using HighlightedItems pattern)
                var itemRect = item.GetClientRect();
                var itemCenter = itemRect.Center;
                var windowOffset = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
                var clickPos = itemCenter + windowOffset;

                // Move mouse to item
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)clickPos.X, (int)clickPos.Y);
                await Task.Delay(20, ct);

                // Ctrl+Shift+Click to stash item (Path of Exile shortcut for moving to stash)
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                await Task.Delay(5, ct);
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                await Task.Delay(5, ct);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(20, ct);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(5, ct);
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                await Task.Delay(5, ct);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                await Task.Delay(30, ct);

                LogDebug($"📦 Auto-stash: Stashed item {i + 1}/{inventoryItems.Count}");
            }

            // Restore mouse position
            System.Windows.Forms.Cursor.Position = prevMousePos;

            LogMessage($"📦 Auto-stash: Finished stashing items");
        }
        catch (Exception ex)
        {
            LogError($"📦 Auto-stash: Error stashing items: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if inventory can fit a 4 tall x 2 wide item
    /// </summary>
    private bool CanInventoryFit4x2Item()
    {
        try
        {
            var inventoryItems = GameController.IngameState.ServerData.PlayerInventories[0].Inventory.InventorySlotItems;

            // Track each inventory slot (12 wide x 5 tall)
            bool[,] inventorySlot = new bool[12, 5];

            // Mark all used slots
            foreach (var inventoryItem in inventoryItems)
            {
                int x = inventoryItem.PosX;
                int y = inventoryItem.PosY;
                int height = inventoryItem.SizeY;
                int width = inventoryItem.SizeX;
                
                for (int row = x; row < x + width && row < 12; row++)
                {
                    for (int col = y; col < y + height && col < 5; col++)
                    {
                        inventorySlot[row, col] = true;
                    }
                }
            }

            // Check if there's space for a 2x4 item (2 wide, 4 tall)
            for (int x = 0; x <= 12 - 2; x++) // Need 2 width
            {
                for (int y = 0; y <= 5 - 4; y++) // Need 4 height
                {
                    bool canFit = true;
                    for (int row = x; row < x + 2; row++)
                    {
                        for (int col = y; col < y + 4; col++)
                        {
                            if (inventorySlot[row, col])
                            {
                                canFit = false;
                                break;
                            }
                        }
                        if (!canFit) break;
                    }
                    if (canFit)
                    {
                        return true; // Found space for 2x4 item
                    }
                }
            }

            return false; // No space for 2x4 item
        }
        catch (Exception ex)
        {
            LogError($"📦 Auto-stash: Error checking inventory space: {ex.Message}");
            return true; // On error, assume we can fit (don't auto-stash)
        }
    }

    /// <summary>
    /// Checks if auto-stash should be triggered and starts it if needed (blocks until complete)
    /// </summary>
    public async Task CheckAndTriggerAutoStashAsync()
    {
        if (_autoStashInProgress)
        {
            LogMessage("📦 Auto-stash: Already in progress, waiting...");
            // Wait for current stash to complete
            while (_autoStashInProgress)
            {
                await Task.Delay(500);
            }
            return;
        }

        if (!CanInventoryFit4x2Item())
        {
            LogMessage("📦 Auto-stash: Inventory full (can't fit 2x4 item), triggering auto-stash...");
            await StartAutoStashAsync(); // This will block until complete
        }
    }
}

