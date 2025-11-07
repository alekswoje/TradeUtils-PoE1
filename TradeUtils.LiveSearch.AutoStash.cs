using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;

namespace TradeUtils;

public partial class TradeUtils
{
    /// <summary>
    /// Execute the complete auto stash routine
    /// </summary>
    private async Task ExecuteAutoStashRoutine()
    {
        try
        {
            _autoStashInProgress = true;
            _autoStashStartTime = DateTime.Now;

            LogMessage("📦 AUTO STASH: Starting auto stash routine...");

            // Add overall timeout (5 minutes)
            var timeoutTask = Task.Delay(300000); // 5 minutes
            var stashTask = ExecuteAutoStashSteps();
            
            var completedTask = await Task.WhenAny(stashTask, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                LogMessage("⏱️ AUTO STASH: Process timed out after 5 minutes");
                return;
            }

            await stashTask; // Get any exceptions from the actual task
        }
        catch (Exception ex)
        {
            LogError($"❌ AUTO STASH ERROR: {ex.Message}");
        }
        finally
        {
            // Resume auto TP
            _autoTpPaused = false;
            _autoStashInProgress = false;
            LogMessage("✅ AUTO STASH: Resumed auto teleporting");
        }
    }

    /// <summary>
    /// Execute the individual steps of the auto stash routine
    /// </summary>
    private async Task ExecuteAutoStashSteps()
    {
        // Pre-check: Ensure we're not in a loading state
        if (GameController.IsLoading || !GameController.InGame)
        {
            LogMessage("⚠️ AUTO STASH: Cannot proceed - game is loading or not in game");
            return;
        }
        
        // Step 1: Check if inventory is full (2x4 space check)
        if (!IsInventoryFullFor2x4Item())
        {
            return;
        }

        LogMessage("📦 AUTO STASH: Inventory is full (no 2x4 space), proceeding with stash routine...");

        // Step 2: Pause auto TP
        _autoTpPaused = true;
        LogMessage("⏸️ AUTO STASH: Paused auto teleporting");

        // Step 3: Go to hideout
        LogMessage("🏠 AUTO STASH: Teleporting to hideout...");
        await TravelToHideoutForStash();

        // Post-travel check: Ensure we're still in a valid state
        if (GameController.IsLoading || !GameController.InGame)
        {
            LogMessage("⚠️ AUTO STASH: Game entered loading state after travel, aborting");
            return;
        }

        // Step 4: Find and click stash
        LogMessage("🔍 AUTO STASH: Looking for stash...");
        if (!await FindAndClickStash())
        {
            LogMessage("❌ AUTO STASH: Failed to find or click stash");
            return;
        }

        // Step 5: Wait for stash to open
        LogMessage("⏳ AUTO STASH: Waiting for stash to open...");
        if (!await WaitForStashToOpen())
        {
            LogMessage("❌ AUTO STASH: Stash failed to open");
            return;
        }

        // Step 6: Dump items to stash
        LogMessage("📥 AUTO STASH: Dumping items to stash...");
        await DumpItemsToStash();

        LogMessage("✅ AUTO STASH: Auto stash routine completed successfully!");
    }

    /// <summary>
    /// Check if inventory is full (specifically checking for 2x4 item space)
    /// </summary>
    private bool IsInventoryFullFor2x4Item()
    {
        try
        {
            var inventoryItems = GameController.IngameState.ServerData.PlayerInventories[0].Inventory.InventorySlotItems;
            
            // Track each inventory slot (12x5 grid)
            bool[,] inventorySlot = new bool[12, 5];

            // Mark used slots
            foreach (var inventoryItem in inventoryItems)
            {
                int x = inventoryItem.PosX;
                int y = inventoryItem.PosY;
                int height = inventoryItem.SizeY;
                int width = inventoryItem.SizeX;
                
                for (int row = x; row < x + width; row++)
                {
                    for (int col = y; col < y + height; col++)
                    {
                        if (row >= 0 && row < 12 && col >= 0 && col < 5)
                        {
                            inventorySlot[row, col] = true;
                        }
                    }
                }
            }

            // Check if there's space for a 2x4 item
            for (int x = 0; x <= 12 - 2; x++) // 2 width
            {
                for (int y = 0; y <= 5 - 4; y++) // 4 height
                {
                    bool canFit = true;
                    for (int checkX = x; checkX < x + 2; checkX++)
                    {
                        for (int checkY = y; checkY < y + 4; checkY++)
                        {
                            if (inventorySlot[checkX, checkY])
                            {
                                canFit = false;
                                break;
                            }
                        }
                        if (!canFit) break;
                    }
                    
                    if (canFit)
                    {
                        return false; // Found space, inventory is NOT full
                    }
                }
            }

            return true; // No space found, inventory IS full
        }
        catch (Exception ex)
        {
            LogError($"❌ AUTO STASH: Error checking inventory fullness: {ex.Message}");
            return false; // Assume not full if we can't check
        }
    }

    /// <summary>
    /// Travel to hideout for stash operation
    /// </summary>
    private async Task TravelToHideoutForStash()
    {
        try
        {
            // Press Enter to open chat
            try
            {
                keybd_event(0x0D, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero); // Enter key down
                await Task.Delay(50);
                keybd_event(0x0D, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Enter key up
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                LogError($"❌ AUTO STASH: Error pressing Enter: {ex.Message}");
                return;
            }

            // Type /hideout command using SendKeys
            string hideoutCommand = "/hideout";
            try
            {
                SendKeys.SendWait(hideoutCommand + "{ENTER}");
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                LogError($"❌ AUTO STASH: Error typing hideout command: {ex.Message}");
                return;
            }

            // Wait for area change (loading screen)
            await WaitForAreaLoad();
        }
        catch (Exception ex)
        {
            LogError($"❌ AUTO STASH: Error traveling to hideout: {ex.Message}");
        }
    }

    /// <summary>
    /// Wait for area to finish loading
    /// </summary>
    private async Task WaitForAreaLoad()
    {
        int maxWaitTime = 30000; // 30 seconds max
        int waitTime = 0;
        
        // Wait for any loading to complete first
        while (waitTime < maxWaitTime)
        {
            bool isLoading = GameController.IsLoading;
            
            if (isLoading)
            {
                await Task.Delay(500);
                waitTime += 500;
            }
            else
            {
                break;
            }
        }
        
        // Now wait for area to stabilize
        waitTime = 0;
        while (waitTime < maxWaitTime)
        {
            bool isLoading = GameController.IsLoading;
            bool inGame = GameController.InGame;
            string currentArea = GameController?.Area?.CurrentArea?.Name ?? "unknown";
            
            if (!isLoading && inGame && !string.IsNullOrEmpty(currentArea) && currentArea != "unknown")
            {
                break;
            }
            else
            {
                await Task.Delay(500);
                waitTime += 500;
            }
        }
        
        LogMessage("✅ AUTO STASH: Area load timeout, continuing anyway...");
    }

    /// <summary>
    /// Find and click the stash
    /// </summary>
    private async Task<bool> FindAndClickStash()
    {
        try
        {
            // Try multiple times to find the stash
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                var itemsOnGround = GameController.IngameState.IngameUi.ItemsOnGroundLabels;
                
                if (itemsOnGround == null)
                {
                    await Task.Delay(1000);
                    continue;
                }
                
                int stashFound = 0;
                foreach (var label in itemsOnGround)
                {
                    if (label?.Label?.Text == "Stash")
                    {
                        stashFound++;
                        var position = label.Label.GetClientRect().Center;
                        LogMessage($"✅ AUTO STASH: Found stash, clicking...");
                        
                        // Click on the stash
                        try
                        {
                            System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)position.X, (int)position.Y);
                            await Task.Delay(100);
                            
                            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                            await Task.Delay(50);
                            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                        }
                        catch (Exception ex)
                        {
                            LogError($"❌ AUTO STASH: Error clicking on stash: {ex.Message}");
                            return false;
                        }
                        
                        return true;
                    }
                }

                await Task.Delay(1000);
            }

            LogMessage("❌ AUTO STASH: Stash not found after 5 attempts");
            return false;
        }
        catch (Exception ex)
        {
            LogError($"❌ AUTO STASH: Error finding/clicking stash: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Wait for stash to open
    /// </summary>
    private async Task<bool> WaitForStashToOpen()
    {
        int maxWaitTime = 10000; // 10 seconds max
        int waitTime = 0;
        
        while (waitTime < maxWaitTime)
        {
            bool isVisible = GameController.IngameState.IngameUi.StashElement.IsVisible;
            
            if (isVisible)
            {
                return true;
            }
            
            await Task.Delay(200);
            waitTime += 200;
        }
        
        LogMessage("❌ AUTO STASH: Stash failed to open within timeout");
        return false;
    }

    /// <summary>
    /// Dump items from inventory to stash
    /// </summary>
    private async Task DumpItemsToStash()
    {
        try
        {
            var inventoryItems = GameController.IngameState.ServerData.PlayerInventories[0].Inventory.InventorySlotItems
                .Where(x => !IsInIgnoreCell(x))
                .OrderBy(x => x.PosX)
                .ThenBy(x => x.PosY)
                .ToList();

            int itemsProcessed = 0;
            foreach (var item in inventoryItems)
            {
                itemsProcessed++;
                
                if (!GameController.IngameState.IngameUi.InventoryPanel.IsVisible)
                {
                    LogMessage("⚠️ AUTO STASH: Inventory panel closed, stopping item dump");
                    break;
                }

                if (!GameController.IngameState.IngameUi.StashElement.IsVisible)
                {
                    LogMessage("⚠️ AUTO STASH: Stash closed, stopping item dump");
                    break;
                }

                await MoveItemToStash(item);
                await Task.Delay(200); // Delay between items
            }

            LogMessage("✅ AUTO STASH: Item dump completed");
        }
        catch (Exception ex)
        {
            LogError($"❌ AUTO STASH: Error dumping items: {ex.Message}");
        }
    }

    /// <summary>
    /// Move a single item to stash
    /// </summary>
    private async Task MoveItemToStash(ServerInventory.InventSlotItem item)
    {
        try
        {
            var itemRect = item.GetClientRect();
            var itemPosition = itemRect.Center;

            // Press Ctrl and Shift keys
            try
            {
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                await Task.Delay(10);

                // Move mouse to item
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)itemPosition.X, (int)itemPosition.Y);
                await Task.Delay(20);

                // Click
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(5);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                await Task.Delay(5);

                // Release Ctrl and Shift keys
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                LogError($"❌ AUTO STASH: Error during item move: {ex.Message}");
                // Try to release keys if they're still pressed
                try
                {
                    keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                }
                catch { }
                throw;
            }
        }
        catch (Exception ex)
        {
            LogError($"❌ AUTO STASH: Error moving item: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if item is in ignore cell
    /// </summary>
    private bool IsInIgnoreCell(ServerInventory.InventSlotItem inventItem)
    {
        var inventPosX = inventItem.PosX;
        var inventPosY = inventItem.PosY;

        if (inventPosX < 0 || inventPosX >= 12)
            return true;
        if (inventPosY < 0 || inventPosY >= 5)
            return true;

        // For now, we don't have ignore cells configured, so return false
        // This can be extended later if needed
        return false;
    }
}

