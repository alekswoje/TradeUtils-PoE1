using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using RectangleF = SharpDX.RectangleF;

namespace TradeUtils;

public partial class TradeUtils
{
    // Currency Exchange specific fields
    private readonly ConcurrentDictionary<RectangleF, bool?> _currencyExchangeMouseStateForRect = new();
    private bool _currencyExchangeButtonImageLoaded = false;
    
    partial void InitializeCurrencyExchange()
    {
        try
        {
            LogMessage("=== Currency Exchange Initialization Started ===");
            
            // Try to load button image
            var imagePath = System.IO.Path.Combine(DirectoryFullName, "Images", "pick.png");
            if (System.IO.File.Exists(imagePath))
            {
                try
                {
                    Graphics.InitImage("pick.png", false);
                    _currencyExchangeButtonImageLoaded = true;
                    LogMessage("✓ Button image loaded for Currency Exchange");
                }
                catch (Exception ex)
                {
                    LogError($"Failed to load button image: {ex.Message}");
                }
            }
            
            LogMessage("=== Currency Exchange Initialization Complete ===");
            LogMessage($"Configuration:");
            LogMessage($"  - Debug Mode: {CurrencyExchangeSettings.DebugMode.Value}");
            LogMessage($"  - Show Button: {CurrencyExchangeSettings.ShowButton.Value}");
            LogMessage($"  - Auto Undercut: {CurrencyExchangeSettings.AutoUndercut.Value}");
            LogMessage($"  - Undercut Amount: {CurrencyExchangeSettings.UndercutAmount.Value}");
        }
        catch (Exception ex)
        {
            LogError($"Failed to initialize Currency Exchange: {ex.Message}");
        }
    }
    
    partial void RenderCurrencyExchange()
    {
        try
        {
            // Only render if Currency Exchange panel is visible
            var currencyExchangePanel = GameController?.IngameState?.IngameUi?.CurrencyExchangePanel;
            if (currencyExchangePanel == null || !currencyExchangePanel.IsVisible)
            {
                return;
            }
            
            if (!CurrencyExchangeSettings.ShowButton.Value)
            {
                return;
            }
            
            // Check if selector is visible (I Have / I Want popup)
            if (IsCurrencyExchangeSelectorVisible())
            {
                return;
            }
            
            // Get the offered item count input field
            var offeredItemCountInput = currencyExchangePanel.OfferedItemCountInput;
            if (offeredItemCountInput == null)
            {
                return;
            }
            
            // Calculate button position: centered above the input field
            var inputRect = offeredItemCountInput.GetClientRectCache;
            const int buttonSize = 37;
            var buttonX = inputRect.X + (inputRect.Width - buttonSize) / 2;
            var buttonY = inputRect.Y - buttonSize;
            var buttonRect = new RectangleF(buttonX, buttonY, buttonSize, buttonSize);
            
            // Render the button
            if (_currencyExchangeButtonImageLoaded)
            {
                Graphics.DrawImage("pick.png", buttonRect);
            }
            else
            {
                // Fallback: Draw a colored box
                Graphics.DrawBox(buttonRect, SharpDX.Color.Orange);
            }
            
            // Check if button is pressed
            if (IsCurrencyExchangeButtonPressed(buttonRect))
            {
                LogMessage("🔘 CURRENCY EXCHANGE: Button pressed!");
                _ = Task.Run(async () =>
                {
                    // Wait for mouse release
                    while (System.Windows.Forms.Control.MouseButtons == System.Windows.Forms.MouseButtons.Left)
                    {
                        await Task.Delay(10);
                    }
                    
                    await AutoFillCurrencyExchange();
                });
            }
        }
        catch (Exception ex)
        {
            LogError($"Error rendering Currency Exchange: {ex.Message}");
        }
    }
    
    partial void AreaChangeCurrencyExchange(AreaInstance area)
    {
        // Currency Exchange doesn't need area change handling currently
    }
    
    partial void DisposeCurrencyExchange()
    {
        try
        {
            LogMessage("Currency Exchange: Disposing...");
            // Clean up any resources
            LogMessage("Currency Exchange: Disposed successfully");
        }
        catch (Exception ex)
        {
            LogError($"Error disposing Currency Exchange: {ex.Message}");
        }
    }
    
    partial void TickCurrencyExchange()
    {
        // Currency Exchange doesn't need tick logic currently
        // All logic is triggered by button press in Render
    }
    
    /// <summary>
    /// Main auto-fill logic for Currency Exchange
    /// </summary>
    private async Task AutoFillCurrencyExchange()
    {
        try
        {
            LogMessage("💱 CURRENCY EXCHANGE: Starting auto-fill...");
            
            var currencyExchangePanel = GameController.IngameState.IngameUi.CurrencyExchangePanel;
            if (currencyExchangePanel == null || !currencyExchangePanel.IsVisible)
            {
                LogMessage("⚠️ Currency Exchange panel not visible");
                return;
            }
            
            // Get the offered item type (what we're selling)
            var offeredItemType = currencyExchangePanel.OfferedItemType;
            if (offeredItemType == null)
            {
                LogMessage("⚠️ No offered item type selected - please select what you're selling first");
                return;
            }
            
            // Get the wanted item type (what we want in return)
            var wantedItemType = currencyExchangePanel.WantedItemType;
            if (wantedItemType == null)
            {
                LogMessage("⚠️ No wanted item type selected - please select what you want first");
                return;
            }
            
            LogMessage($"📊 Offered Item: {offeredItemType.BaseName}");
            LogMessage($"📊 Wanted Item: {wantedItemType.BaseName}");
            
            // Count total amount in inventory
            int totalOfferedAmount = GetTotalItemAmount(offeredItemType);
            LogMessage($"📦 Total {offeredItemType.BaseName} in inventory: {totalOfferedAmount}");
            
            if (totalOfferedAmount == 0)
            {
                LogMessage("⚠️ No items found in inventory");
                return;
            }
            
                // Get best maker order ratio from the market
                var bestRatio = await GetBestMakerOrderRatio(currencyExchangePanel);
                if (!bestRatio.HasValue)
                {
                    LogMessage("⚠️ Could not determine best maker order ratio - aborting");
                    LogMessage("💡 Try hovering over Market Ratio or close/reopen the Currency Exchange panel");
                    return;
                }
                
                LogMessage($"💰 Best maker order ratio: {bestRatio.Value:F4} (they give 1, we give {bestRatio.Value:F2})");
                
                // Calculate undercut using batch logic
                // Example: if maker is 1:48 and we have 48000, we do 999:48000 instead of 1000:48000
                int wantedAmount;
                int offeredAmount = totalOfferedAmount;
                
                if (CurrencyExchangeSettings.AutoUndercut.Value && bestRatio.HasValue)
                {
                    // Calculate how many batches at the maker ratio
                    int batches = (int)Math.Floor(totalOfferedAmount / bestRatio.Value);
                    
                    if (batches > 1)
                    {
                        // Undercut by using (batches - 1) for the same amount
                        wantedAmount = batches - 1;
                        LogMessage($"📉 Undercutting: {batches} batches → {wantedAmount} wanted (ratio: {wantedAmount}:{offeredAmount})");
                    }
                    else
                    {
                        // Not enough to undercut by batch, use direct ratio
                        wantedAmount = Math.Max(1, (int)Math.Floor(totalOfferedAmount / bestRatio.Value));
                        LogMessage($"⚠️ Not enough for batch undercut, using direct ratio: {wantedAmount}:{offeredAmount}");
                    }
                }
                else
                {
                    // No undercutting, match the maker ratio exactly
                    wantedAmount = Math.Max(1, (int)Math.Floor(totalOfferedAmount / bestRatio.Value));
                    LogMessage($"📊 Matching maker ratio: {wantedAmount}:{offeredAmount}");
                }
                
                wantedAmount = Math.Max(1, wantedAmount); // Minimum 1
                
                LogMessage($"💱 Final trade: {offeredAmount} {offeredItemType.BaseName} → {wantedAmount} {wantedItemType.BaseName}");
                LogMessage($"📊 Effective ratio: {wantedAmount}:{offeredAmount} (they give {wantedAmount}, we give {offeredAmount})");
            
            // Fill the input fields
            if (CurrencyExchangeSettings.FillOfferedAmount.Value)
            {
                await FillOfferedAmountField(offeredAmount);
            }
            
            if (CurrencyExchangeSettings.FillWantedAmount.Value)
            {
                await FillWantedAmountField(wantedAmount);
            }
            
            // Auto-click place order if enabled
            if (CurrencyExchangeSettings.AutoClickPlaceOrder.Value)
            {
                await ClickPlaceOrderButton();
            }
            
            LogMessage("✅ Currency Exchange auto-fill complete");
        }
        catch (Exception ex)
        {
            LogError($"❌ Currency Exchange auto-fill failed: {ex.Message}");
            LogError($"StackTrace: {ex.StackTrace}");
        }
    }
    
    /// <summary>
    /// Get the best maker order ratio from the currency exchange market tooltip
    /// </summary>
    private async Task<float?> GetBestMakerOrderRatio(dynamic currencyExchangePanel)
    {
        try
        {
            LogMessage("🔍 Scanning market for maker orders...");
            
            // Find the Market Ratio element
            dynamic marketRatioElement = null;
            string takerRatioText = null;
            
            // Search through children for "Market Ratio" text
            foreach (var child in currencyExchangePanel.Children)
            {
                if (child == null) continue;
                
                var text = child.Text?.ToString() ?? "";
                if (text.Contains("Market Ratio"))
                {
                    marketRatioElement = child;
                    takerRatioText = text;
                    LogMessage($"📊 Found Market Ratio element: {text}");
                    break;
                }
            }
            
            if (marketRatioElement == null || string.IsNullOrEmpty(takerRatioText))
            {
                LogMessage("⚠️ Market Ratio element not found");
                return null;
            }
            
            // Move mouse to Market Ratio element and hold Alt to trigger tooltip FIRST
            var marketRatioRect = marketRatioElement.GetClientRectCache;
            var windowOffset = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
            var center = marketRatioRect.Center;
            var hoverPos = new System.Numerics.Vector2(center.X + windowOffset.X, center.Y + windowOffset.Y);
            
            LogMessage($"🖱️ Hovering over Market Ratio at ({hoverPos.X:F0}, {hoverPos.Y:F0})");
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)hoverPos.X, (int)hoverPos.Y);
            await Task.Delay(50); // Wait for hover to register
            
            // Hold Alt key
            LogMessage("⌨️ Holding Alt key...");
            keybd_event(VK_ALT, 0, 0, UIntPtr.Zero);
            await Task.Delay(150); // Wait for tooltip to appear
            
            // Try to get the tooltip multiple times with retries
            dynamic tooltip = null;
            for (int retry = 0; retry < 5; retry++)
            {
                tooltip = marketRatioElement.Tooltip;
                if (tooltip != null && tooltip.IsVisible)
                {
                    LogMessage($"✅ Tooltip visible on attempt {retry + 1}");
                    break;
                }
                await Task.Delay(50);
            }
            
            if (tooltip == null || !tooltip.IsVisible)
            {
                // Release Alt key
                keybd_event(VK_ALT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                LogMessage("⚠️ Tooltip not visible after multiple attempts - cannot get maker orders");
                return null;
            }
            
            // Parse the taker ratio from the text (e.g., "{1} : {50}" means 1:50)
            LogMessage($"📝 Raw taker ratio text: '{takerRatioText}'");
            var takerRatio = ParseRatioFromText(takerRatioText);
            if (takerRatio == null || !takerRatio.HasValue)
            {
                // Release Alt key
                keybd_event(VK_ALT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                LogMessage("⚠️ Could not parse taker ratio from Market Ratio text");
                return null;
            }
            
            LogMessage($"📊 Current taker ratio: {takerRatio.Value.want}:{takerRatio.Value.have} (we give {takerRatio.Value.have} to get {takerRatio.Value.want})");
            
            // Parse all maker orders from tooltip children (scan recursively)
            var makerOrders = new List<(float want, float have, float ratio)>();
            
            // Debug: Log tooltip structure
            if (CurrencyExchangeSettings.DebugMode.Value)
            {
                LogMessage($"📋 Tooltip has {tooltip.Children?.Count ?? 0} children");
                int childIndex = 0;
                if (tooltip.Children != null)
                {
                    foreach (var child in tooltip.Children)
                    {
                        try
                        {
                            var childText = child?.Text?.ToString() ?? "";
                            LogMessage($"  Child {childIndex}: Text='{(childText.Length > 50 ? childText.Substring(0, 50) + "..." : childText)}'");
                            childIndex++;
                        }
                        catch { }
                    }
                }
            }
            
            // Check if tooltip has children (sometimes it doesn't load properly)
            if (tooltip.Children == null || tooltip.Children.Count == 0)
            {
                // Release Alt key
                keybd_event(VK_ALT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                LogMessage("⚠️ Tooltip has no children - try closing and reopening the Currency Exchange panel");
                return null;
            }
            
            ScanTooltipForMakerOrders(tooltip, makerOrders, 0);
            
            if (makerOrders.Count == 0)
            {
                // Release Alt key
                keybd_event(VK_ALT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                LogMessage("⚠️ No maker orders found in tooltip - unable to determine best price");
                return null;
            }
            
            // Sort maker orders by ratio (descending = highest ratio first)
            var sortedOrders = makerOrders.OrderByDescending(o => o.ratio).ToList();
            
            LogMessage($"📊 Found {sortedOrders.Count} maker orders:");
            for (int i = 0; i < Math.Min(10, sortedOrders.Count); i++)
            {
                var order = sortedOrders[i];
                LogMessage($"  {i + 1}. {order.want:F2}:{order.have:F2} (ratio: {order.ratio:F4})");
            }
            
            // Find the best maker order that's better than the taker ratio (lower ratio = better for us as sellers)
            // We want the highest ratio that's still LOWER than taker (to be at top of book)
            float takerRatioValue = (float)takerRatio.Value.have / (float)takerRatio.Value.want;
            LogMessage($"🎯 Taker ratio value: {takerRatioValue:F4}, looking for highest maker below this");
            
            var bestMakerOrder = sortedOrders.FirstOrDefault(o => o.ratio < takerRatioValue);
            
            if (bestMakerOrder == default)
            {
                // No maker order better than taker, use the highest available
                bestMakerOrder = sortedOrders.First();
                LogMessage($"⚠️ No maker order better than taker ({takerRatioValue:F4}), using highest: {bestMakerOrder.want:F2}:{bestMakerOrder.have:F2}");
            }
            else
            {
                LogMessage($"✅ Best maker order (top of book): {bestMakerOrder.want:F2}:{bestMakerOrder.have:F2} (ratio: {bestMakerOrder.ratio:F4})");
            }
            
            // Release Alt key
            keybd_event(VK_ALT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            
            return bestMakerOrder.ratio;
        }
        catch (Exception ex)
        {
            // Make sure to release Alt key on any error
            try
            {
                keybd_event(VK_ALT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
            
            LogError($"❌ Error scanning maker orders: {ex.Message}");
            LogError($"StackTrace: {ex.StackTrace}");
            return null;
        }
    }
    
    /// <summary>
    /// Recursively scan tooltip element for maker orders
    /// </summary>
    private void ScanTooltipForMakerOrders(dynamic element, List<(float want, float have, float ratio)> makerOrders, int depth)
    {
        try
        {
            if (element == null || depth > 5) return; // Prevent infinite recursion
            
            // Try to parse text from this element
            try
            {
                var text = element.Text?.ToString() ?? "";
                if (!string.IsNullOrEmpty(text))
                {
                    var parsedRatio = ParseRatioFromText(text);
                    
                    // Use pattern matching to avoid dynamic binding issues
                    if (parsedRatio is (float want, float have))
                    {
                        // Calculate ratio as want/have (how many of ours for 1 of theirs)
                        float ratioValue = have / want;
                        makerOrders.Add((want, have, ratioValue));
                        
                        if (CurrencyExchangeSettings.DebugMode.Value)
                        {
                            LogMessage($"🔍 Found maker order: {want:F2}:{have:F2} (ratio: {ratioValue:F4})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log parsing errors in debug mode
                if (CurrencyExchangeSettings.DebugMode.Value)
                {
                    LogMessage($"⚠️ Error parsing element text at depth {depth}: {ex.Message}");
                }
            }
            
            // Recursively scan children
            try
            {
                if (element.Children != null)
                {
                    foreach (var child in element.Children)
                    {
                        ScanTooltipForMakerOrders(child, makerOrders, depth + 1);
                    }
                }
            }
            catch
            {
                // Ignore children enumeration errors
            }
        }
        catch (Exception ex)
        {
            // Ignore errors during scanning
            if (CurrencyExchangeSettings.DebugMode.Value)
            {
                LogMessage($"⚠️ Error scanning element at depth {depth}: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Parse ratio from text like "{1} : {50}" or "1:50" or "{< 1} : {1110.26}"
    /// Returns floats to preserve decimal precision
    /// </summary>
    private (float want, float have)? ParseRatioFromText(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            
            // Save original for debug
            var originalText = text;
            
            // Remove special tags like <<market_ratio>>
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<<[^>]+>>", "");
            
            // Remove color tags (only proper tag names, not "< " which is part of the ratio)
            // Match tags like <kalguurlightgrey> but not < followed by space
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<[a-z]+>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            // Remove braces
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\{|\}", "");
            
            // Remove "< " and "> " prefixes (for ratios like "< 1" or "> Market Ratio")
            text = text.Replace("< ", "").Replace("> ", "");
            
            // Remove "Market Ratio" text if present
            text = text.Replace("Market Ratio", "").Trim();
            
            if (CurrencyExchangeSettings.DebugMode.Value)
            {
                LogMessage($"📝 After cleanup: '{text}' (from '{originalText}')");
            }
            
            // Look for pattern "X : Y" or "X:Y" (with optional decimals)
            var match = System.Text.RegularExpressions.Regex.Match(text, @"([\d.]+)\s*:\s*([\d.]+)");
            if (match.Success)
            {
                if (float.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float want) &&
                    float.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float have))
                {
                    if (want > 0 && have > 0)
                    {
                        if (CurrencyExchangeSettings.DebugMode.Value)
                        {
                            LogMessage($"🔍 Parsed ratio from text: {want:F2}:{have:F2} (from {want}:{have})");
                        }
                        return (want, have);
                    }
                }
            }
            
            // Only log if it looks like it might have been a ratio
            if (text.Contains(":") && CurrencyExchangeSettings.DebugMode.Value)
            {
                LogMessage($"⚠️ Could not parse ratio from: '{text}'");
            }
            
            return null;
        }
        catch (Exception ex)
        {
            if (CurrencyExchangeSettings.DebugMode.Value)
            {
                LogError($"❌ Error parsing ratio: {ex.Message}");
            }
            return null;
        }
    }
    
    /// <summary>
    /// Get total amount of item in inventory/stash
    /// </summary>
    private int GetTotalItemAmount(ExileCore.PoEMemory.Models.BaseItemType offeredItemType)
    {
        try
        {
            int amount = 0;
            var processedInventories = new HashSet<string>();
            var targetBaseName = offeredItemType.BaseName;
            
            LogDebug($"🔍 Scanning inventories for: {targetBaseName}");
            
            foreach (var playerInventory in GameController.IngameState.ServerData.PlayerInventories)
            {
                var inventory = playerInventory?.Inventory;
                if (inventory?.Items == null) continue;
                
                // Avoid double counting with unique inventory key
                var inventoryKey = $"{inventory.InventType}_{inventory.Address}";
                if (!processedInventories.Add(inventoryKey)) continue;
                
                // Filter inventory types (Currency, Essence, etc.)
                var inventoryTypeString = inventory.InventType.ToString();
                LogDebug($"  Checking inventory type: {inventoryTypeString}");
                
                // TODO: Add proper inventory type filtering based on settings
                
                foreach (var item in inventory.Items)
                {
                    if (item == null) continue;
                    
                    var baseItemType = GameController.Files.BaseItemTypes.Translate(item.Metadata);
                    if (baseItemType?.BaseName == targetBaseName)
                    {
                        var stack = item.GetComponent<ExileCore.PoEMemory.Components.Stack>();
                        int itemAmount = stack?.Size ?? 1;
                        amount += itemAmount;
                        LogDebug($"  Found {itemAmount}x {targetBaseName} in {inventoryTypeString}");
                    }
                }
            }
            
            return amount;
        }
        catch (Exception ex)
        {
            LogError($"Error counting items: {ex.Message}");
            return 0;
        }
    }
    
    /// <summary>
    /// Fill the offered amount input field
    /// </summary>
    private async Task FillOfferedAmountField(int amount)
    {
        try
        {
            var currencyExchangePanel = GameController.IngameState.IngameUi.CurrencyExchangePanel;
            var offeredItemCountInput = currencyExchangePanel?.OfferedItemCountInput;
            if (offeredItemCountInput == null) return;
            
            LogMessage($"⌨️ Filling offered amount: {amount}");
            
            // Get input field position (center)
            var inputRect = offeredItemCountInput.GetClientRectCache;
            var windowOffset = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
            var center = inputRect.Center;
            var clickPos = new System.Numerics.Vector2(center.X + windowOffset.X, center.Y + windowOffset.Y);
            
            // Move mouse to input field
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)clickPos.X, (int)clickPos.Y);
            await Task.Delay(CurrencyExchangeSettings.ActionDelay.Value + new Random().Next(0, CurrencyExchangeSettings.RandomDelay.Value));
            
            // Click to focus input
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(10);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(50);
            
            // Clear existing value with backspace
            for (int i = 0; i < 6; i++)
            {
                System.Windows.Forms.SendKeys.SendWait("{BACKSPACE}");
                await Task.Delay(10);
            }
            
            // Type the new amount
            System.Windows.Forms.SendKeys.SendWait(amount.ToString());
            await Task.Delay(CurrencyExchangeSettings.ActionDelay.Value);
            
            LogMessage($"✅ Filled offered amount: {amount}");
        }
        catch (Exception ex)
        {
            LogError($"Error filling offered amount: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Fill the wanted amount input field
    /// </summary>
    private async Task FillWantedAmountField(int amount)
    {
        try
        {
            var currencyExchangePanel = GameController.IngameState.IngameUi.CurrencyExchangePanel;
            var wantedItemCountInput = currencyExchangePanel?.WantedItemCountInput;
            if (wantedItemCountInput == null) return;
            
            LogMessage($"⌨️ Filling wanted amount: {amount}");
            
            // Get input field position (center)
            var inputRect = wantedItemCountInput.GetClientRectCache;
            var windowOffset = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
            var center = inputRect.Center;
            var clickPos = new System.Numerics.Vector2(center.X + windowOffset.X, center.Y + windowOffset.Y);
            
            // Move mouse to input field
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)clickPos.X, (int)clickPos.Y);
            await Task.Delay(CurrencyExchangeSettings.ActionDelay.Value + new Random().Next(0, CurrencyExchangeSettings.RandomDelay.Value));
            
            // Click to focus input
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(10);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(50);
            
            // Clear existing value with backspace
            for (int i = 0; i < 6; i++)
            {
                System.Windows.Forms.SendKeys.SendWait("{BACKSPACE}");
                await Task.Delay(10);
            }
            
            // Special handling: Click to the right and back to clear any auto-filled values
            var clickPosRight = new System.Numerics.Vector2(center.X + windowOffset.X + inputRect.Width / 2 + 5, center.Y + windowOffset.Y);
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)clickPosRight.X, (int)clickPosRight.Y);
            await Task.Delay(20);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(10);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(20);
            
            // Click back on input field
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)clickPos.X, (int)clickPos.Y);
            await Task.Delay(20);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(10);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            await Task.Delay(50);
            
            // Type the new amount
            System.Windows.Forms.SendKeys.SendWait(amount.ToString());
            await Task.Delay(CurrencyExchangeSettings.ActionDelay.Value);
            
            LogMessage($"✅ Filled wanted amount: {amount}");
        }
        catch (Exception ex)
        {
            LogError($"Error filling wanted amount: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Click the Place Order button
    /// </summary>
    private async Task ClickPlaceOrderButton()
    {
        try
        {
            LogMessage("🔘 Looking for Place Order button...");
            
            var placeOrderButton = GetPlaceOrderButton();
            if (placeOrderButton == null)
            {
                LogMessage("⚠️ Place Order button not found");
                return;
            }
            
            // Get button position
            var buttonRect = placeOrderButton.GetClientRectCache;
            var windowOffset = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
            var buttonCenter = buttonRect.Center;
            var clickPos = new System.Numerics.Vector2(buttonCenter.X + windowOffset.X, buttonCenter.Y + windowOffset.Y);
            
            LogMessage($"🖱️ Clicking Place Order button at ({clickPos.X}, {clickPos.Y})");
            
            // Move mouse to button
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point((int)clickPos.X, (int)clickPos.Y);
            await Task.Delay(CurrencyExchangeSettings.ActionDelay.Value + new Random().Next(0, CurrencyExchangeSettings.RandomDelay.Value));
            
            LogMessage("✅ Moved mouse to Place Order button (ready to click)");
            LogMessage("💡 Click manually to confirm the order");
            
            // Note: Not auto-clicking by default for safety
            // User can enable AutoClickPlaceOrder in settings if desired
        }
        catch (Exception ex)
        {
            LogError($"Error clicking place order: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Get the Place Order button element
    /// </summary>
    private dynamic GetPlaceOrderButton()
    {
        try
        {
            var currencyExchangePanel = GameController.IngameState.IngameUi.CurrencyExchangePanel;
            if (currencyExchangePanel == null || !currencyExchangePanel.IsVisible) return null;
            
            foreach (var child in currencyExchangePanel.Children)
            {
                if (child == null) continue;
                
                foreach (var grandchild in child.Children)
                {
                    if (grandchild == null) continue;
                    
                    // Look for "place order" text (case insensitive)
                    if (grandchild.Text?.ToLowerInvariant() == "place order")
                    {
                        LogDebug($"✅ Found Place Order button");
                        return grandchild;
                    }
                }
            }
            
            LogDebug("⚠️ Place Order button not found in UI hierarchy");
            return null;
        }
        catch (Exception ex)
        {
            LogError($"Error finding place order button: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Check if currency selector (I Have / I Want) is visible
    /// </summary>
    private bool IsCurrencyExchangeSelectorVisible()
    {
        try
        {
            var currencyExchangePanel = GameController.IngameState.IngameUi.CurrencyExchangePanel;
            if (currencyExchangePanel == null || !currencyExchangePanel.IsVisible) return false;
            
            foreach (var child in currencyExchangePanel.Children)
            {
                foreach (var grandchild in child.Children)
                {
                    if (grandchild.Text == "I Have" || grandchild.Text == "I Want")
                    {
                        return grandchild.IsVisible;
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
    
    /// <summary>
    /// Check if button is pressed (same logic as POE2 version)
    /// </summary>
    private bool IsCurrencyExchangeButtonPressed(RectangleF buttonRect)
    {
        try
        {
            var prevState = _currencyExchangeMouseStateForRect.GetValueOrDefault(buttonRect);
            var cursorPos = System.Windows.Forms.Cursor.Position;
            var windowOffset = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
            var relativeCursorX = cursorPos.X - windowOffset.X;
            var relativeCursorY = cursorPos.Y - windowOffset.Y;
            
            // Check if cursor is inside button rectangle
            var isHovered = relativeCursorX >= buttonRect.X && 
                           relativeCursorX <= buttonRect.X + buttonRect.Width &&
                           relativeCursorY >= buttonRect.Y && 
                           relativeCursorY <= buttonRect.Y + buttonRect.Height;
            if (!isHovered)
            {
                _currencyExchangeMouseStateForRect[buttonRect] = null;
                return false;
            }
            
            var isPressed = System.Windows.Forms.Control.MouseButtons == System.Windows.Forms.MouseButtons.Left;
            _currencyExchangeMouseStateForRect[buttonRect] = isPressed;
            
            // Button press detected on transition from not pressed to pressed
            return isPressed && prevState == false;
        }
        catch (Exception ex)
        {
            LogError($"Error checking button press: {ex.Message}");
            return false;
        }
    }
}

