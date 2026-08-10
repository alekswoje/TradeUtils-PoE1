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
    private async Task<bool> StartAutoStashAsync()
    {
        if (_autoStashInProgress)
        {
            LogMessage("Auto-stash already in progress");
            return false;
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

        bool stashed = false;

        try
        {
            LogMessage("📦 Auto-stash: Starting...");
            stashed = await AutoStashAsync(_autoStashCancellationToken.Token);
            if (stashed) LogMessage("✅ Auto-stash: Completed successfully");
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

        return stashed;
    }

    /// <summary>
    /// Main auto-stash logic: goes to hideout, opens stash, and stashes all items
    /// </summary>
    private async Task<bool> AutoStashAsync(CancellationToken ct)
    {
        // The countdown exists so a user who alt-tabbed can get back to the game. When the window
        // is already focused — which it always is when BulkBuy triggers this — it is five seconds
        // of nothing.
        if (!GameController.Window.IsForeground())
        {
            LogMessage("📦 Auto-stash: starting in 5 seconds... (tab back into game)");
            for (int i = 5; i > 0 && !ct.IsCancellationRequested; i--)
                await Task.Delay(1000, ct);
        }

        if (ct.IsCancellationRequested) return false;

        // Step 0: get the trade window out of the way. After a purchase the merchant panel is still
        // covering the screen, and a click aimed at the stash behind it just hits the panel — which
        // is why stashing failed with the merchant open.
        await CloseBlockingPanelsAsync(ct);

        // Step 1: travel home, unless we already know we're standing in our OWN hideout.
        //
        // "Am I in a hideout" is not the same question: right after buying, the character is in the
        // seller's hideout, where there is no stash of ours to click. Only the plugin's own
        // /hideout marks the current area as home.
        if (IsInOwnHideout())
        {
            LogMessage("📦 Auto-stash: already home, skipping travel.");
        }
        else
        {
            LogMessage(IsInHideout()
                ? "📦 Auto-stash: in someone else's hideout — going home first."
                : "📦 Auto-stash: typing /hideout...");

            await TypeHideoutCommandAsync(ct);

            LogMessage("📦 Auto-stash: waiting for the hideout to load...");
            await WaitForHideoutLoadAsync(ct);
            await Task.Delay(2000, ct);

            // The zone change resets this to false, so claim it only once we have actually arrived.
            if (IsInHideout()) _inOwnHideout = true;
        }

        // Step 2: entities have to exist before the stash can be found.
        await WaitForEntitiesToLoadAsync(ct);

        // Travelling opens nothing, but a panel left over from before the trip still blocks clicks.
        await CloseBlockingPanelsAsync(ct);

        // Step 3: click the stash and confirm it actually opened, retrying the CLICK.
        //
        // This is the fix for auto-stash giving up after one miss. FindAndClickStashAsync returns
        // true as soon as it has *dispatched* a click — it cannot know whether the click landed —
        // so the old retry loop wrapped around it always succeeded first time, and a missed click
        // fell straight through to a hard return. The travel step then ran again from the top on
        // the next trigger. Retrying the click is what was wanted all along.
        const int maxAttempts = 3;
        bool stashOpen = false;

        for (int attempt = 1; attempt <= maxAttempts && !stashOpen; attempt++)
        {
            if (ct.IsCancellationRequested) return false;

            if (IsStashOpen())
            {
                LogMessage("📦 Auto-stash: stash is already open.");
                stashOpen = true;
                break;
            }

            LogMessage($"📦 Auto-stash: clicking the stash (attempt {attempt}/{maxAttempts})...");

            if (!await FindAndClickStashAsync(ct, attempt))
            {
                LogMessage($"📦 Auto-stash: couldn't find a stash to click on attempt {attempt}.");
                await Task.Delay(700, ct);
                continue;
            }

            // First attempt gets longer: a click on a distant stash makes the character walk there.
            stashOpen = await WaitForStashToOpenAsync(ct, attempt == 1 ? 8000 : 5000);

            if (!stashOpen && attempt < maxAttempts)
                LogMessage($"📦 Auto-stash: the stash didn't open, clicking again ({attempt + 1}/{maxAttempts}).");
        }

        if (!stashOpen)
        {
            LogError($"📦 Auto-stash: the stash didn't open after {maxAttempts} clicks.");
            return false;
        }

        LogMessage("📦 Auto-stash: stashing inventory items...");
        await StashAllInventoryItemsAsync(ct);
        return true;
    }

    /// <summary>
    /// Whether the character is standing in the player's OWN hideout, as far as the plugin knows.
    ///
    /// Conservative by design: this is only true after the plugin's own /hideout has landed, and any
    /// zone change clears it. A false negative costs one trip home; a false positive sends the stash
    /// routine looking for the player's stash in a stranger's hideout.
    /// </summary>
    private bool IsInOwnHideout() => _inOwnHideout && IsInHideout();

    /// <summary>
    /// Closes the merchant/trade panel if it is open, so a click aimed at something in the world
    /// isn't swallowed by it. Returns true when nothing is blocking any more.
    /// </summary>
    private async Task<bool> CloseBlockingPanelsAsync(CancellationToken ct)
    {
        const int maxAttempts = 3;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested) return false;

            bool blocked;
            try
            {
                var purchaseWindow = GameController?.IngameState?.IngameUi?.PurchaseWindowHideout;
                blocked = purchaseWindow != null && purchaseWindow.IsVisible;
            }
            catch
            {
                return true;
            }

            if (!blocked) return true;

            // Only ever pressed while a panel is confirmed open — with nothing open, Escape opens
            // the game menu, which would be a blocking panel of its own.
            if (!CanSendInput("close the trade window")) return false;

            LogMessage($"📦 Auto-stash: the trade window is still open, closing it (attempt {attempt}/{maxAttempts}).");

            keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
            await Task.Delay(40, ct);
            keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            await Task.Delay(400, ct);
        }

        LogError("📦 Auto-stash: couldn't close the trade window; a click at the stash would hit it instead.");
        return false;
    }

    /// <summary>Whether the character is standing in a hideout right now — anyone's.</summary>
    private bool IsInHideout()
    {
        try
        {
            if (GameController.IsLoading) return false;

            var area = GameController.Area?.CurrentArea;
            if (area == null) return false;

            return area.IsHideout ||
                   (area.DisplayName?.Contains("Hideout", StringComparison.OrdinalIgnoreCase) ?? false);
        }
        catch
        {
            return false;
        }
    }

    private bool IsStashOpen()
    {
        try
        {
            return GameController.IngameState.IngameUi.StashElement.IsVisible;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Types /hideout command in chat
    /// </summary>
    private async Task TypeHideoutCommandAsync(CancellationToken ct)
    {
        // Typing into an unfocused game means typing "/hideout" plus two Enters into whatever app
        // is in front instead.
        if (!CanSendInput("type /hideout")) return;

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
    /// Finds the nearest visible stash and clicks it.
    ///
    /// Returns whether a click was *dispatched*, which is not the same as the stash opening — the
    /// caller has to confirm that separately. Treating this return value as "the stash is open" is
    /// what made a missed click look like a success.
    ///
    /// <paramref name="attempt"/> switches strategy rather than repeating one that just failed:
    /// the projected world position first, then the on-screen label, which is the more reliable
    /// target when the stash is plainly visible but the world projection lands somewhere odd.
    /// </summary>
    private async Task<bool> FindAndClickStashAsync(CancellationToken ct, int attempt = 1)
    {
        if (!CanSendInput("click the stash")) return false;

        bool preferLabel = attempt > 1;

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

            // Step 6: Click the stash's projected world position — unless a previous attempt
            // already tried that and the stash didn't open, in which case go straight to the label.
            if (preferLabel)
            {
                LogMessage("📦 Auto-stash: world-position click didn't open it, trying the label.");
            }
            else
            {
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
                        LogMessage($"📦 Auto-stash: clicking the stash at world position ({worldPos.X:F1}, {worldPos.Y:F1}, {worldPos.Z:F1}) -> screen ({clickPos.X}, {clickPos.Y})");

                        System.Windows.Forms.Cursor.Position = clickPos;
                        await Task.Delay(150, ct);
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                        await Task.Delay(50, ct);
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                        await Task.Delay(500, ct);

                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LogDebug($"📦 Auto-stash: couldn't use the world position ({ex.Message}), trying the label.");
                }
            }

            // Click the on-screen label instead.
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
    private async Task<bool> WaitForStashToOpenAsync(CancellationToken ct, int maxWait = 10000)
    {
        // Wait a bit first to ensure we're fully loaded into hideout
        await Task.Delay(500, ct);

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

        LogDebug($"📦 Auto-stash: stash still not open after {maxWait}ms");
        return false;
    }

    /// <summary>
    /// Stashes all inventory items (using HighlightedItems logic)
    /// </summary>
    private async Task StashAllInventoryItemsAsync(CancellationToken ct)
    {
        if (!CanSendInput("ctrl+click items into the stash")) return;

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
    /// <summary>
    /// Stashes if the inventory is out of room. Returns true if there is space afterwards, so a
    /// caller can tell "stashed" from "tried and failed" rather than re-checking and guessing.
    /// </summary>
    public async Task<bool> CheckAndTriggerAutoStashAsync()
    {
        if (_autoStashInProgress)
        {
            LogMessage("📦 Auto-stash: already running, waiting for it to finish...");

            // Bounded: an unbounded wait on a flag that another task owns hangs the caller forever
            // if that task dies without clearing it.
            for (int waited = 0; _autoStashInProgress && waited < 120_000; waited += 500)
                await Task.Delay(500);

            if (_autoStashInProgress)
            {
                LogError("📦 Auto-stash: the running stash never finished; giving up on it.");
                return false;
            }

            return CanInventoryFit4x2Item();
        }

        if (CanInventoryFit4x2Item()) return true;

        LogMessage("📦 Auto-stash: inventory is full, stashing...");
        await StartAutoStashAsync();

        return CanInventoryFit4x2Item();
    }
}

