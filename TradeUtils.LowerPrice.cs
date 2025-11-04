using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
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
using NAudio.Wave;
using System.Net.Http;
using System.Text.Json;
using TradeUtils.Utility;
using RectangleF = SharpDX.RectangleF;

namespace TradeUtils;

public partial class TradeUtils
{
    // LowerPrice-specific fields
    private readonly ConcurrentDictionary<RectangleF, bool?> _lowerPriceMouseStateForRect = new();
    private readonly Random _lowerPriceRandom = new Random();
    private DateTime _lowerPriceLastRepriceTime = DateTime.MinValue;
    private bool _lowerPriceTimerExpired = false;
    private WaveOutEvent _lowerPriceWaveOut;
    private bool _lowerPriceManualRepriceTriggered = false;
    private bool _lowerPriceWasStashVisible = false;
    private int _lowerPriceButtonRenderCount = 0;
    private bool _lowerPriceImageLoaded = false;
    private int _lowerPriceRenderCallCount = 0;
    private DateTime _lowerPriceLastRenderLog = DateTime.MinValue;
    private DateTime _lowerPriceLastPanelCheck = DateTime.MinValue;
    
    // Value display fields
    private readonly HttpClient _lowerPriceHttpClient = new HttpClient();
    private DateTime _lowerPriceLastCurrencyUpdate = DateTime.MinValue;
    private Dictionary<string, decimal> _lowerPriceCurrencyRates = new Dictionary<string, decimal>();
    private readonly object _lowerPriceCurrencyRatesLock = new object();

    private bool LowerPriceMoveCancellationRequested => (Control.MouseButtons & MouseButtons.Right) != 0;

    partial void InitializeLowerPrice()
    {
        try
        {
            var imagePath = Path.Combine(DirectoryFullName, "Images", "pick.png");
            LogMessage($"LowerPrice DEBUG: Loading image from: {imagePath}");
            LogMessage($"LowerPrice DEBUG: Image file exists? {File.Exists(imagePath)}");
            
            if (File.Exists(imagePath))
            {
                try
                {
                    // Try loading with just the filename (Graphics might expect relative path)
                    Graphics.InitImage("pick.png", false);
                    _lowerPriceImageLoaded = true;
                    LogMessage("LowerPrice DEBUG: Image loaded successfully (using 'pick.png')");
                }
                catch (Exception ex)
                {
                    LogError($"LowerPrice DEBUG: Failed to load image: {ex.Message}");
                    try
                    {
                        // Try with full path
                        Graphics.InitImage(imagePath, false);
                        _lowerPriceImageLoaded = true;
                        LogMessage("LowerPrice DEBUG: Image loaded successfully (using full path)");
                    }
                    catch (Exception ex2)
                    {
                        LogError($"LowerPrice DEBUG: Failed to load image with full path: {ex2.Message}");
                    }
                }
            }
            else
            {
                LogError($"LowerPrice DEBUG: Image file not found at {imagePath}");
                LogError($"LowerPrice DEBUG: Will use fallback colored box for button");
            }
            
            // Initialize currency rates with default values
            InitializeLowerPriceDefaultCurrencyRates();
            
            // Load currency rates from API/local file
            _ = Task.Run(async () => await UpdateLowerPriceCurrencyRates());
            
            LogMessage("LowerPrice sub-plugin initialized");
        }
        catch (Exception ex)
        {
            LogError($"Failed to initialize LowerPrice: {ex.Message}\nStackTrace: {ex.StackTrace}");
        }
    }

    partial void RenderLowerPrice()
    {
        // Ensure LowerPrice is enabled
        if (!LowerPriceSettings.Enable.Value)
        {
            return;
        }
        
        try
        {
            // Check hotkeys first
            CheckLowerPriceHotkeys();

            // Render timer display
            if (LowerPriceSettings.EnableTimer.Value && LowerPriceSettings.ShowTimerCountdown.Value)
            {
                RenderLowerPriceTimerDisplay();
            }

            // Render value display
            if (LowerPriceSettings.ShowValueDisplay.Value)
            {
                RenderLowerPriceValueDisplay();
            }

            // Render button for offline merchant panel (hideout trading post)
            var ingameState = GameController?.IngameState;
            if (ingameState == null)
            {
                return; // Not in game yet
            }
            
            var ingameUi = ingameState.IngameUi;
            if (ingameUi == null)
            {
                return; // UI not ready
            }
            
            // POE1: Use OfflineMerchantPanel for the hideout trading post
            var offlineMerchantPanel = ingameUi.OfflineMerchantPanel;
            
            // Debug panel state (only log once per state change)
            bool isPanelVisible = offlineMerchantPanel != null && offlineMerchantPanel.IsVisible;
            if (isPanelVisible != _lowerPriceWasStashVisible)
            {
                LogMessage($"LowerPrice DEBUG: Panel visibility changed - OfflineMerchantPanel null? {offlineMerchantPanel == null}, IsVisible={isPanelVisible}");
                if (isPanelVisible)
                {
                    LogMessage($"LowerPrice DEBUG: OfflineMerchantPanel address = {offlineMerchantPanel.Address:X}");
                    LogMessage($"LowerPrice DEBUG: OfflineMerchantPanel IsVisible={offlineMerchantPanel.IsVisible}");
                    
                    try
                    {
                        // OfflineMerchantPanel is a StashElement, so we need to access VisibleStash first
                        var visibleStash = offlineMerchantPanel.VisibleStash;
                        LogMessage($"LowerPrice DEBUG: VisibleStash null? {visibleStash == null}");
                        if (visibleStash != null)
                        {
                            var items = visibleStash.VisibleInventoryItems;
                            LogMessage($"LowerPrice DEBUG: VisibleInventoryItems null? {items == null}");
                            if (items != null)
                            {
                                LogMessage($"LowerPrice DEBUG: Item count = {items.Count()}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($"LowerPrice DEBUG: Error accessing OfflineMerchantPanel data: {ex.Message}");
                    }
                }
                _lowerPriceWasStashVisible = isPanelVisible;
            }
            
            if (offlineMerchantPanel != null && offlineMerchantPanel.IsVisible)
            {
                const float buttonSize = 37;
                var offset = new Vector2(10, 10);
                var windowTopLeft = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
                var buttonPos = new Vector2(windowTopLeft.X, windowTopLeft.Y) + offset;
                var buttonRect = new RectangleF(buttonPos.X, buttonPos.Y, buttonSize, buttonSize);
                
                // Debug button rendering on first render
                if (_lowerPriceButtonRenderCount < 3)
                {
                    _lowerPriceButtonRenderCount++;
                    LogMessage($"LowerPrice DEBUG: Rendering button - Pos=({buttonPos.X}, {buttonPos.Y}), Size={buttonSize}");
                    LogMessage($"LowerPrice DEBUG: Window TopLeft=({windowTopLeft.X}, {windowTopLeft.Y})");
                    LogMessage($"LowerPrice DEBUG: Image loaded? {_lowerPriceImageLoaded}");
                }
                
                try
                {
                    if (_lowerPriceImageLoaded)
                    {
                        try
                        {
                            Graphics.DrawImage("pick.png", buttonRect);
                            
                            // Debug: Log once that we're drawing the image
                            if (_lowerPriceButtonRenderCount == 1)
                            {
                                LogMessage($"LowerPrice DEBUG: Drawing image 'pick.png' successfully");
                            }
                        }
                        catch (Exception imgEx)
                        {
                            LogError($"LowerPrice DEBUG: Failed to draw image, using fallback: {imgEx.Message}");
                            // Fallback if image draw fails
                            Graphics.DrawBox(buttonRect, new SharpDX.Color(100, 150, 255, 200));
                            Graphics.DrawFrame(buttonRect, new SharpDX.Color(255, 255, 255, 255), 2);
                            var textPos = new Vector2(buttonPos.X + 8, buttonPos.Y + 12);
                            Graphics.DrawText("RP", textPos, new SharpDX.Color(255, 255, 255, 255));
                            _lowerPriceImageLoaded = false; // Don't try again
                        }
                    }
                    else
                    {
                        // Fallback: Draw a colored box with border
                        Graphics.DrawBox(buttonRect, new SharpDX.Color(100, 150, 255, 200));
                        Graphics.DrawFrame(buttonRect, new SharpDX.Color(255, 255, 255, 255), 2);
                        // Draw text "RP" (Reprice) in center
                        var textPos = new Vector2(buttonPos.X + 8, buttonPos.Y + 12);
                        Graphics.DrawText("RP", textPos, new SharpDX.Color(255, 255, 255, 255));
                    }
                }
                catch (Exception ex)
                {
                    LogError($"LowerPrice DEBUG: Failed to draw button: {ex.Message}");
                }

                // Check for button press or manual trigger
                var buttonPressed = IsLowerPriceButtonPressed(buttonRect);
                if (buttonPressed || _lowerPriceManualRepriceTriggered)
                {
                    LogMessage($"LowerPrice DEBUG: Button pressed={buttonPressed}, ManualTrigger={_lowerPriceManualRepriceTriggered}");
                    _lowerPriceManualRepriceTriggered = false; // Reset manual trigger
                    _ = Task.Run(async () =>
                    {
                        while (Control.MouseButtons == MouseButtons.Left)
                        {
                            await Task.Delay(10);
                        }
                        UpdateLowerPriceAllItemPrices(offlineMerchantPanel);
                    });
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"Error in RenderLowerPrice: {ex.Message}");
        }
    }

    partial void AreaChangeLowerPrice(AreaInstance area)
    {
        // LowerPrice doesn't need area change handling currently
    }

    partial void DisposeLowerPrice()
    {
        try
        {
            _lowerPriceWaveOut?.Dispose();
            _lowerPriceHttpClient?.Dispose();
        }
        catch (Exception ex)
        {
            LogError($"Error disposing LowerPrice: {ex.Message}");
        }
    }

    partial void TickLowerPrice()
    {
        // LowerPrice doesn't need tick currently
        // All periodic updates are handled in Render()
    }

    private void CheckLowerPriceHotkeys()
    {
        try
        {
            // Check manual reprice hotkey
            if (LowerPriceSettings.ManualRepriceHotkey.PressedOnce())
            {
                LogMessage("LowerPrice DEBUG: Manual reprice hotkey pressed!");
                _lowerPriceManualRepriceTriggered = true;
            }
        }
        catch (Exception ex)
        {
            LogError($"Error checking LowerPrice hotkeys: {ex.Message}");
        }
    }

    private async void UpdateLowerPriceAllItemPrices(object offlineMerchantPanel)
    {
        try
        {
            LogMessage("=== LowerPrice: Starting reprice operation ===");
            
            // POE1: Use OfflineMerchantPanel for offline merchant panel
            var panel = GameController.IngameState.IngameUi.OfflineMerchantPanel;
            
            if (panel == null)
            {
                LogError("LowerPrice DEBUG: OfflineMerchantPanel is null");
                return;
            }
            
            if (!panel.IsVisible)
            {
                LogError("LowerPrice DEBUG: OfflineMerchantPanel is not visible");
                return;
            }
            
            // OfflineMerchantPanel is a StashElement, so we need to access VisibleStash first
            var visibleStash = panel.VisibleStash;
            if (visibleStash == null)
            {
                LogError("LowerPrice DEBUG: VisibleStash is null");
                return;
            }
            
            var items = visibleStash.VisibleInventoryItems;
            
            if (items == null)
            {
                LogError("LowerPrice DEBUG: VisibleInventoryItems is null");
                return;
            }
            
            var itemCount = items.Count();
            LogMessage($"LowerPrice DEBUG: Found {itemCount} items in merchant panel");
            
            if (!items.Any())
            {
                LogMessage("LowerPrice DEBUG: No items to process");
                return;
            }

            int processedCount = 0;
            int skippedLocked = 0;
            int skippedNoPrice = 0;
            int repriced = 0;
            int pickedUp = 0;
            bool structureDumped = false;  // Only dump structure once for first item
            
            foreach (var item in items)
            {
                try
                {
                    processedCount++;
                    
                    if (!panel.IsVisible || LowerPriceMoveCancellationRequested)
                    {
                        LogMessage($"LowerPrice DEBUG: Breaking - PanelVisible={panel.IsVisible}, CancelRequested={LowerPriceMoveCancellationRequested}");
                        break;
                    }

                    if (item.Children?.Count == 2)
                    {
                        LogMessage($"LowerPrice DEBUG: Item {processedCount} - Skipping (has 2 children)");
                        await TaskUtils.NextFrame();
                        await Task.Delay(LowerPriceSettings.ActionDelay.Value + _lowerPriceRandom.Next(LowerPriceSettings.RandomDelay.Value));
                        continue;
                    }

                    var itemOffset = new Vector2(5, 5);
                    var itemRect = item.GetClientRectCache;
                    var position = new Vector2(itemRect.TopLeft.X, itemRect.TopLeft.Y) + itemOffset + 
                                   new Vector2(GameController.Window.GetWindowRectangleTimeCache.TopLeft.X, 
                                             GameController.Window.GetWindowRectangleTimeCache.TopLeft.Y);

                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - Moving mouse to position ({position.X}, {position.Y})");
                    Utility.Mouse.moveMouse(position);
                    await TaskUtils.NextFrame();
                    await Task.Delay(LowerPriceSettings.ActionDelay.Value + _lowerPriceRandom.Next(LowerPriceSettings.RandomDelay.Value));

                    // Check if item is locked before processing
                    if (IsLowerPriceItemLocked(item))
                    {
                        LogMessage($"LowerPrice DEBUG: Item {processedCount} - Skipping (locked)");
                        skippedLocked++;
                        await TaskUtils.NextFrame();
                        await Task.Delay(LowerPriceSettings.ActionDelay.Value + _lowerPriceRandom.Next(LowerPriceSettings.RandomDelay.Value));
                        continue;
                    }

                    var tooltip = item.Tooltip;
                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - Tooltip null? {tooltip == null}");
                    
                    // Dump tooltip structure for first item with tooltip
                    if (tooltip != null && !structureDumped)
                    {
                        structureDumped = true;
                        LogMessage($"LowerPrice DEBUG: === DUMPING TOOLTIP STRUCTURE FOR FIRST ITEM ===");
                        DumpTooltipStructure(tooltip);
                        LogMessage($"LowerPrice DEBUG: === END TOOLTIP STRUCTURE ===");
                    }
                    
                    if (tooltip != null && tooltip.Children.Count > 0)
                    {
                        LogMessage($"LowerPrice DEBUG: Item {processedCount} - Tooltip.Children.Count = {tooltip.Children.Count}");
                        var tooltipChild0 = tooltip.Children[0];
                        if (tooltipChild0 != null && tooltipChild0.Children.Count > 1)
                        {
                            LogMessage($"LowerPrice DEBUG: Item {processedCount} - Tooltip.Children[0].Children.Count = {tooltipChild0.Children.Count}");
                            var tooltipChild1 = tooltipChild0.Children[1];
                            if (tooltipChild1 != null && tooltipChild1.Children.Any())
                            {
                                LogMessage($"LowerPrice DEBUG: Item {processedCount} - Tooltip.Children[0].Children[1].Children.Count = {tooltipChild1.Children.Count}");
                                var lastChild = tooltipChild1.Children.Last();
                                LogMessage($"LowerPrice DEBUG: Item {processedCount} - LastChild.Children.Count = {lastChild?.Children?.Count ?? -1}");
                                if (lastChild != null && lastChild.Children.Count > 1)
                                {
                                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - LastChild.Children[1] exists? {lastChild.Children.Count > 1}");
                                    var priceChild1 = lastChild.Children[1];
                                    if (priceChild1 != null && priceChild1.Children.Count > 0)
                                    {
                                        LogMessage($"LowerPrice DEBUG: Item {processedCount} - PriceChild1.Children.Count = {priceChild1.Children.Count}");
                                        var priceChild0 = priceChild1.Children[0];
                                        if (priceChild0 != null)
                                        {
                                            string priceText = priceChild0.Text;
                                            LogMessage($"LowerPrice DEBUG: Item {processedCount} - PriceText = '{priceText}'");
                                            if (priceText != null && priceText.EndsWith("x"))
                                            {
                                                string priceStr = priceText.Replace("x", "").Replace(",", "").Trim();
                                                LogMessage($"LowerPrice DEBUG: Item {processedCount} - Parsed priceStr = '{priceStr}'");
                                                if (int.TryParse(priceStr, out int oldPrice))
                                                {
                                                    string orbType = priceChild1.Children.Count > 2 ? priceChild1.Children[2].Text : null;
                                                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - OldPrice = {oldPrice}, OrbType = '{orbType}'");
                                                    bool reprice = false;
                                                    if (orbType == "Chaos Orb" && LowerPriceSettings.RepriceChaos.Value) reprice = true;
                                                    else if (orbType == "Divine Orb" && LowerPriceSettings.RepriceDivine.Value) reprice = true;
                                                    else if (orbType == "Exalted Orb" && LowerPriceSettings.RepriceExalted.Value) reprice = true;
                                                    else if (orbType == "Orb of Annulment" && LowerPriceSettings.RepriceAnnul.Value) reprice = true;

                                                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - Reprice = {reprice}");
                                                    if (!reprice)
                                                    {
                                                        skippedNoPrice++;
                                                        continue;
                                                    }

                                                    float newPrice = CalculateLowerPriceNewPrice(oldPrice, orbType);
                                                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - Calculated newPrice = {newPrice}");
                                                    
                                                    if (oldPrice == 1)
                                                    {
                                                        LogMessage($"LowerPrice DEBUG: Item {processedCount} - Price is 1, PickupItemsAtOne = {LowerPriceSettings.PickupItemsAtOne.Value}");
                                                        if (LowerPriceSettings.PickupItemsAtOne.Value)
                                                        {
                                                            LogMessage($"LowerPrice DEBUG: Item {processedCount} - Picking up item");
                                                            Utility.Keyboard.KeyDown(Keys.LControlKey);
                                                            await TaskUtils.NextFrame();
                                                            await Task.Delay(LowerPriceSettings.ActionDelay.Value + _lowerPriceRandom.Next(LowerPriceSettings.RandomDelay.Value));
                                                            Utility.Mouse.LeftDown();
                                                            await TaskUtils.NextFrame();
                                                            await Task.Delay(LowerPriceSettings.ActionDelay.Value + _lowerPriceRandom.Next(LowerPriceSettings.RandomDelay.Value));
                                                            Utility.Mouse.LeftUp();
                                                            await TaskUtils.NextFrame();
                                                            await Task.Delay(LowerPriceSettings.ActionDelay.Value + _lowerPriceRandom.Next(LowerPriceSettings.RandomDelay.Value));
                                                            Utility.Keyboard.KeyUp(Keys.LControlKey);
                                                            await TaskUtils.NextFrame();
                                                            await Task.Delay(LowerPriceSettings.ActionDelay.Value + _lowerPriceRandom.Next(LowerPriceSettings.RandomDelay.Value));
                                                            pickedUp++;
                                                        }
                                                        continue;
                                                    }

                                                    if (newPrice < 1) newPrice = 1;
                                                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - Repricing from {oldPrice} to {newPrice}");
                                                    Utility.Mouse.RightDown();
                                                    await TaskUtils.NextFrame();
                                                    await Task.Delay(LowerPriceSettings.ActionDelay.Value + _lowerPriceRandom.Next(LowerPriceSettings.RandomDelay.Value));
                                                    Utility.Mouse.RightUp();
                                                    await TaskUtils.NextFrame();
                                                    await Task.Delay(LowerPriceSettings.ActionDelay.Value + _lowerPriceRandom.Next(LowerPriceSettings.RandomDelay.Value));
                                                    Utility.Keyboard.Type($"{newPrice}");
                                                    await TaskUtils.NextFrame();
                                                    await Task.Delay(LowerPriceSettings.ActionDelay.Value + _lowerPriceRandom.Next(LowerPriceSettings.RandomDelay.Value));
                                                    Utility.Keyboard.KeyPress(Keys.Enter);
                                                    await TaskUtils.NextFrame();
                                                    await Task.Delay(LowerPriceSettings.ActionDelay.Value + _lowerPriceRandom.Next(LowerPriceSettings.RandomDelay.Value));
                                                    repriced++;
                                                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - Successfully repriced!");
                                                    
                                                    // Update last reprice time and reset timer
                                                    if (LowerPriceSettings.EnableTimer.Value)
                                                    {
                                                        _lowerPriceLastRepriceTime = DateTime.Now;
                                                        _lowerPriceTimerExpired = false;
                                                    }
                                                }
                                                else
                                                {
                                                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - Failed to parse price as int");
                                                    skippedNoPrice++;
                                                }
                                            }
                                            else
                                            {
                                                LogMessage($"LowerPrice DEBUG: Item {processedCount} - PriceText doesn't end with 'x' or is null");
                                                skippedNoPrice++;
                                            }
                                        }
                                        else
                                        {
                                            LogMessage($"LowerPrice DEBUG: Item {processedCount} - priceChild0 is null");
                                            skippedNoPrice++;
                                        }
                                    }
                                    else
                                    {
                                        LogMessage($"LowerPrice DEBUG: Item {processedCount} - priceChild1 is null or has no children");
                                        skippedNoPrice++;
                                    }
                                }
                                else
                                {
                                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - lastChild is null or doesn't have >1 children");
                                    skippedNoPrice++;
                                }
                            }
                            else
                            {
                                LogMessage($"LowerPrice DEBUG: Item {processedCount} - tooltipChild1 is null or has no children");
                                skippedNoPrice++;
                            }
                        }
                        else
                        {
                            LogMessage($"LowerPrice DEBUG: Item {processedCount} - tooltipChild0 is null or doesn't have >1 children");
                            skippedNoPrice++;
                        }
                    }
                    else
                    {
                        LogMessage($"LowerPrice DEBUG: Item {processedCount} - Tooltip is null or has no children");
                        skippedNoPrice++;
                    }

                    await TaskUtils.NextFrame();
                    await Task.Delay(LowerPriceSettings.ActionDelay.Value + _lowerPriceRandom.Next(LowerPriceSettings.RandomDelay.Value));
                }
                catch (Exception ex)
                {
                    // Log error for individual item processing but continue with next item
                    LogError($"LowerPrice DEBUG: Error processing item {processedCount}: {ex.Message}\nStackTrace: {ex.StackTrace}");
                    continue;
                }
            }
            
            // Log summary
            LogMessage($"=== LowerPrice: Reprice operation complete ===");
            LogMessage($"LowerPrice DEBUG: Total items processed: {processedCount}");
            LogMessage($"LowerPrice DEBUG: Items locked: {skippedLocked}");
            LogMessage($"LowerPrice DEBUG: Items without price: {skippedNoPrice}");
            LogMessage($"LowerPrice DEBUG: Items repriced: {repriced}");
            LogMessage($"LowerPrice DEBUG: Items picked up: {pickedUp}");
        }
        catch (Exception ex)
        {
            // Log error for the entire reprice operation
            LogError($"LowerPrice DEBUG: Error in UpdateAllItemPrices: {ex.Message}\nStackTrace: {ex.StackTrace}");
        }
    }

    private float CalculateLowerPriceNewPrice(int oldPrice, string orbType)
    {
        bool useFlatReduction = false;

        // Check for currency-specific overrides first
        switch (orbType)
        {
            case "Divine Orb":
                // Divine Override: if checked, force flat reduction; if unchecked, use global setting
                useFlatReduction = LowerPriceSettings.DivineUseFlat ? true : LowerPriceSettings.UseFlatReduction;
                break;
            case "Chaos Orb":
                // Chaos Override: if checked, force flat reduction; if unchecked, use global setting
                useFlatReduction = LowerPriceSettings.ChaosUseRatio ? true : LowerPriceSettings.UseFlatReduction;
                break;
            case "Exalted Orb":
                // Exalted Override: if checked, force flat reduction; if unchecked, use global setting
                useFlatReduction = LowerPriceSettings.ExaltedUseRatio ? true : LowerPriceSettings.UseFlatReduction;
                break;
            case "Orb of Annulment":
                // Annul Override: if checked, force flat reduction; if unchecked, use global setting
                useFlatReduction = LowerPriceSettings.AnnulUseFlat ? true : LowerPriceSettings.UseFlatReduction;
                break;
            default:
                // Use global setting for unknown currencies
                useFlatReduction = LowerPriceSettings.UseFlatReduction;
                break;
        }

        if (useFlatReduction)
        {
            return oldPrice - LowerPriceSettings.FlatReductionAmount.Value;
        }
        else
        {
            return (float)Math.Floor(oldPrice * LowerPriceSettings.PriceRatio.Value);
        }
    }

    private bool IsLowerPriceButtonPressed(RectangleF buttonRect)
    {
        try
        {
            var prevState = _lowerPriceMouseStateForRect.GetValueOrDefault(buttonRect);
            var cursorPos = Utility.Mouse.GetCursorPosition();
            var windowPos = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
            var relativePosX = cursorPos.X - windowPos.X;
            var relativePosY = cursorPos.Y - windowPos.Y;
            
            // Check if cursor is within button rect bounds
            var isHovered = relativePosX >= buttonRect.X && relativePosX <= (buttonRect.X + buttonRect.Width) &&
                           relativePosY >= buttonRect.Y && relativePosY <= (buttonRect.Y + buttonRect.Height);
            
            if (!isHovered)
            {
                _lowerPriceMouseStateForRect[buttonRect] = null;
                return false;
            }

            var isPressed = Control.MouseButtons == MouseButtons.Left;
            _lowerPriceMouseStateForRect[buttonRect] = isPressed;
            return isPressed && prevState == false;
        }
        catch (Exception ex)
        {
            LogError($"Error checking button press: {ex.Message}");
            return false;
        }
    }

    private void DumpTooltipStructure(dynamic element, string prefix = "", int depth = 0, int maxDepth = 5)
    {
        try
        {
            if (element == null || depth > maxDepth) return;
            
            string text = "";
            try { text = element.Text ?? ""; } catch { }
            
            string textureName = "";
            try { textureName = element.TextureName ?? ""; } catch { }
            
            int childCount = 0;
            try { childCount = element.Children?.Count ?? 0; } catch { }
            
            string info = $"{prefix}[{depth}] Children={childCount}";
            if (!string.IsNullOrEmpty(text))
                info += $", Text='{text}'";
            if (!string.IsNullOrEmpty(textureName))
                info += $", Texture='{textureName}'";
            
            LogMessage($"LowerPrice STRUCTURE: {info}");
            
            if (childCount > 0 && depth < maxDepth)
            {
                try
                {
                    int index = 0;
                    foreach (var child in element.Children)
                    {
                        DumpTooltipStructure(child, $"{prefix}  [{index}]", depth + 1, maxDepth);
                        index++;
                    }
                }
                catch (Exception ex)
                {
                    LogError($"LowerPrice STRUCTURE: Error iterating children at depth {depth}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"LowerPrice STRUCTURE: Error dumping element at depth {depth}: {ex.Message}");
        }
    }

    private bool IsLowerPriceItemLocked(dynamic item)
    {
        try
        {
            // Check all children of the item for locked texture
            if (item?.Children != null)
            {
                foreach (var child in item.Children)
                {
                    if (IsLowerPriceElementOrChildrenLocked(child))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch
        {
            // If any error occurs, assume not locked to avoid blocking legitimate items
            return false;
        }
    }

    private bool IsLowerPriceElementOrChildrenLocked(dynamic element)
    {
        try
        {
            // Check if this element has the locked texture
            if (!string.IsNullOrEmpty(element.TextureName) && 
                element.TextureName.Contains("LockedItems.dds"))
            {
                return true;
            }

            // Recursively check children
            if (element?.Children != null)
            {
                foreach (var child in element.Children)
                {
                    if (IsLowerPriceElementOrChildrenLocked(child))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            // If any error occurs, assume not locked
            return false;
        }
    }

    private void RenderLowerPriceTimerDisplay()
    {
        try
        {
            if (_lowerPriceLastRepriceTime == DateTime.MinValue)
            {
                // No reprice yet, show ready status
                var pos = new Vector2(10, 60);
                Graphics.DrawText("Timer: READY", pos);
                return;
            }

            var timeSinceLastReprice = DateTime.Now - _lowerPriceLastRepriceTime;
            var timerDuration = TimeSpan.FromMinutes(LowerPriceSettings.TimerDurationMinutes.Value);
            var timeRemaining = timerDuration - timeSinceLastReprice;

            if (timeRemaining <= TimeSpan.Zero)
            {
                // Timer expired
                if (!_lowerPriceTimerExpired)
                {
                    _lowerPriceTimerExpired = true;
                    if (LowerPriceSettings.EnableSoundNotification.Value)
                    {
                        PlayLowerPriceSoundNotification();
                    }
                }
                
                var pos = new Vector2(10, 60);
                Graphics.DrawText("Timer: EXPIRED - Ready to reprice!", pos);
            }
            else
            {
                // Timer still running
                var pos = new Vector2(10, 60);
                var timeText = $"Timer: {timeRemaining:mm\\:ss} remaining";
                Graphics.DrawText(timeText, pos);
            }
        }
        catch (Exception ex)
        {
            LogError($"Error rendering timer display: {ex.Message}");
        }
    }

    private void PlayLowerPriceSoundNotification()
    {
        try
        {
            var soundPath = Path.Combine(DirectoryFullName, "sound", "pulse.wav");
            if (File.Exists(soundPath))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        using (var audioFile = new AudioFileReader(soundPath))
                        using (var waveOut = new WaveOutEvent())
                        {
                            waveOut.Init(audioFile);
                            waveOut.Play();
                            while (waveOut.PlaybackState == PlaybackState.Playing)
                            {
                                System.Threading.Thread.Sleep(100);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($"Failed to play sound: {ex.Message}");
                    }
                });
            }
            else
            {
                LogError($"Sound file not found: {soundPath}");
            }
        }
        catch (Exception ex)
        {
            LogError($"Error playing sound notification: {ex.Message}");
        }
    }

    private void InitializeLowerPriceDefaultCurrencyRates()
    {
        lock (_lowerPriceCurrencyRatesLock)
        {
            // Initialize with -1 to indicate rates need to be loaded from API
            _lowerPriceCurrencyRates["chaos_to_divine"] = -1m;
            _lowerPriceCurrencyRates["chaos_to_exalted"] = -1m;
            _lowerPriceCurrencyRates["divine_to_chaos"] = -1m;
            _lowerPriceCurrencyRates["divine_to_exalted"] = -1m;
            _lowerPriceCurrencyRates["exalted_to_chaos"] = -1m;
            _lowerPriceCurrencyRates["exalted_to_divine"] = -1m;
            _lowerPriceCurrencyRates["annul_to_chaos"] = -1m;
            _lowerPriceCurrencyRates["annul_to_divine"] = -1m;
            _lowerPriceCurrencyRates["annul_to_exalted"] = -1m;
        }
    }

    private async Task UpdateLowerPriceCurrencyRates()
    {
        if (!LowerPriceSettings.AutoUpdateRates) return;
        
        var timeSinceUpdate = DateTime.Now - _lowerPriceLastCurrencyUpdate;
        if (timeSinceUpdate.TotalMinutes < LowerPriceSettings.CurrencyUpdateInterval.Value) return;

        try
        {
            // Try API first, fallback to local file
            // Note: POE1 uses different poe.ninja leagues
            JsonDocument jsonDoc;
            try
            {
                // Use Standard league for POE1 - adjust as needed
                var response = await _lowerPriceHttpClient.GetStringAsync("https://poe.ninja/api/data/currencyoverview?league=Standard&type=Currency");
                jsonDoc = JsonDocument.Parse(response);
            }
            catch
            {
                // Fallback to local poeninja.json file
                var localJsonPath = Path.Combine(DirectoryFullName, "poeninja.json");
                if (File.Exists(localJsonPath))
                {
                    var localJson = await File.ReadAllTextAsync(localJsonPath);
                    jsonDoc = JsonDocument.Parse(localJson);
                }
                else
                {
                    LogError("Failed to fetch currency rates from API and local file not found");
                    return;
                }
            }
            
            lock (_lowerPriceCurrencyRatesLock)
            {
                // Parse poe.ninja currency data for POE1 format
                if (jsonDoc.RootElement.TryGetProperty("lines", out var lines))
                {
                    foreach (var line in lines.EnumerateArray())
                    {
                        if (line.TryGetProperty("currencyTypeName", out var currName) &&
                            line.TryGetProperty("chaosEquivalent", out var chaosEq))
                        {
                            string currency = currName.GetString();
                            decimal chaosValue = chaosEq.GetDecimal();
                            
                            if (chaosValue > 0)
                            {
                                _lowerPriceCurrencyRates[$"{currency.ToLower()}_to_chaos"] = chaosValue;
                            }
                        }
                    }
                }
            }
            
            _lowerPriceLastCurrencyUpdate = DateTime.Now;
        }
        catch (Exception ex)
        {
            LogError($"Failed to update currency rates: {ex.Message}");
        }
    }

    private void RenderLowerPriceValueDisplay()
    {
        try
        {
            var offlineMerchantPanel = GameController.IngameState.IngameUi.OfflineMerchantPanel;
            if (offlineMerchantPanel?.IsVisible != true) 
            {
                return;
            }

            // Update currency rates in background
            _ = Task.Run(UpdateLowerPriceCurrencyRates);

            // OfflineMerchantPanel is a StashElement, so we need to access VisibleStash first
            var visibleStash = offlineMerchantPanel.VisibleStash;
            if (visibleStash == null)
            {
                return;
            }
            
            var items = visibleStash.VisibleInventoryItems;
            if (items == null) 
            {
                return;
            }

            var itemValues = CalculateLowerPriceItemValues(items);
            
            var pos = new Vector2(LowerPriceSettings.ValueDisplayX.Value, LowerPriceSettings.ValueDisplayY.Value);
            
            // Create value display text
            var totalItemsInTab = items?.Count() ?? 0;
            var displayText = $"Items in tab: {itemValues.ItemsWithPricing}/{totalItemsInTab}\n";
            displayText += $"Items for sale: {itemValues.TotalItems}\n";
            
            if (itemValues.ChaosTotal > 0)
                displayText += $"Chaos: {itemValues.ChaosTotal:F0}\n";
            if (itemValues.DivineTotal > 0)
                displayText += $"Divines: {itemValues.DivineTotal:F1}\n";
            if (itemValues.ExaltedTotal > 0)
                displayText += $"Exalts: {itemValues.ExaltedTotal:F1}\n";
            if (itemValues.AnnulTotal > 0)
                displayText += $"Annuls: {itemValues.AnnulTotal:F0}\n";
            
            displayText += $"\nTotal in Divine: {itemValues.TotalInDivine:F1}\n";
            displayText += $"Total in Exalts: {itemValues.TotalInExalted:F1}";
            
            // Warning if tooltip count doesn't match total items
            if (items != null)
            {
                var itemsWithTooltips = items.Where(i => i.Tooltip != null).Count();
                var totalItems = items.Count();
                
                if (itemsWithTooltips < totalItems)
                {
                    displayText += $"\n⚠️ Hover over items to load pricing data!";
                }
            }

            // Draw black background - POE1 uses SharpDX.Color
            var textSize = Graphics.MeasureText(displayText);
            var backgroundRect = new RectangleF(pos.X - 5, pos.Y - 5, textSize.X + 10, textSize.Y + 10);
            Graphics.DrawBox(backgroundRect, new SharpDX.Color(0, 0, 0, 180));

            Graphics.DrawText(displayText, pos);
        }
        catch (Exception ex)
        {
            LogError($"Error rendering value display: {ex.Message}");
        }
    }

    private ItemValueSummary CalculateLowerPriceItemValues(IEnumerable<dynamic> items)
    {
        var summary = new ItemValueSummary();
        var totalItemsProcessed = 0;
        var itemsWithTooltips = 0;
        var itemsWithPricing = 0;
        
        try
        {
            foreach (var item in items)
            {
                totalItemsProcessed++;
                
                try
                {
                    // Check if item is locked before processing
                    if (IsLowerPriceItemLocked(item))
                    {
                        continue;
                    }

                    // Check if item has tooltip
                    var tooltip = item.Tooltip;
                    if (tooltip == null) 
                    {
                        continue;
                    }
                    
                    itemsWithTooltips++;
                    
                    if (tooltip.Children == null || tooltip.Children.Count == 0) 
                    {
                        continue;
                    }
                    
                    // Try to find price information in the tooltip structure
                    string priceText = null;
                    string orbType = null;
                    
                    // First try the specific structure
                    if (tooltip.Children?.Count > 0)
                    {
                        var child0 = tooltip.Children[0];
                        if (child0?.Children?.Count > 1)
                        {
                            var child1 = child0.Children[1];
                            if (child1?.Children?.Count > 0)
                            {
                                var lastChild = child1.Children.Last();
                                if (lastChild?.Children?.Count > 1)
                                {
                                    var priceChild = lastChild.Children[1];
                                    if (priceChild?.Children?.Count > 2)
                                    {
                                        priceText = priceChild.Children[0]?.Text;
                                        orbType = priceChild.Children[2]?.Text;
                                    }
                                }
                            }
                        }
                    }
                    
                    if (string.IsNullOrEmpty(priceText) || string.IsNullOrEmpty(orbType))
                    {
                        continue;
                    }
                    
                    if (!priceText.EndsWith("x"))
                    {
                        continue;
                    }
                    
                    string priceStr = priceText.Replace("x", "").Replace(",", "").Trim();
                    if (!int.TryParse(priceStr, out int price)) 
                    {
                        continue;
                    }
                    
                    itemsWithPricing++;
                    summary.TotalItems++;
            
                    lock (_lowerPriceCurrencyRatesLock)
                    {
                        switch (orbType)
                        {
                            case "Chaos Orb":
                                summary.ChaosTotal += price;
                                summary.TotalInDivine += price * GetLowerPriceRate("chaos_to_divine");
                                summary.TotalInExalted += price * GetLowerPriceRate("chaos_to_exalted");
                                break;
                            case "Divine Orb":
                                summary.DivineTotal += price;
                                summary.TotalInDivine += price;
                                summary.TotalInExalted += price * GetLowerPriceRate("divine_to_exalted");
                                break;
                            case "Exalted Orb":
                                summary.ExaltedTotal += price;
                                summary.TotalInDivine += price * GetLowerPriceRate("exalted_to_divine");
                                summary.TotalInExalted += price;
                                break;
                            case "Orb of Annulment":
                                summary.AnnulTotal += price;
                                summary.TotalInDivine += price * GetLowerPriceRate("annul_to_divine");
                                summary.TotalInExalted += price * GetLowerPriceRate("annul_to_exalted");
                                break;
                        }
                    }
                }
                catch
                {
                    // Skip this item if any error occurs
                    continue;
                }
            }
        }
        catch
        {
            // If any major error occurs, return current summary
            return summary;
        }
        
        // Set the processing stats
        summary.TotalItemsProcessed = totalItemsProcessed;
        summary.ItemsWithTooltips = itemsWithTooltips;
        summary.ItemsWithPricing = itemsWithPricing;
        
        return summary;
    }

    private decimal GetLowerPriceRate(string rateKey)
    {
        lock (_lowerPriceCurrencyRatesLock)
        {
            if (_lowerPriceCurrencyRates.TryGetValue(rateKey, out var rate))
            {
                if (rate == -1m)
                {
                    return 0m;
                }
                return rate;
            }
            return 0m;
        }
    }
}

public class ItemValueSummary
{
    public int TotalItems { get; set; }
    public int TotalItemsProcessed { get; set; }
    public int ItemsWithTooltips { get; set; }
    public int ItemsWithPricing { get; set; }
    public decimal ChaosTotal { get; set; }
    public decimal DivineTotal { get; set; }
    public decimal ExaltedTotal { get; set; }
    public decimal AnnulTotal { get; set; }
    public decimal TotalInDivine { get; set; }
    public decimal TotalInExalted { get; set; }
}

