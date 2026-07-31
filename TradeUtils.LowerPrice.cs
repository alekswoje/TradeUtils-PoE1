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

    // Chaos value of ONE unit of each currency, keyed by the in-game display name
    // ("Divine Orb" -> 177.9). Everything is summed in chaos and converted once at the end,
    // which is what the old pairwise "x_to_y" rate table was trying to do — it never worked,
    // because the poe.ninja parser only ever wrote "<name>_to_chaos" keys while the display
    // read "chaos_to_divine"-style keys that nothing populated, so every lookup returned 0.
    private Dictionary<string, decimal> _lowerPriceChaosValues =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
    private readonly object _lowerPriceCurrencyRatesLock = new object();
    private int _lowerPriceRatesFetching; // 0 = idle, 1 = a poe.ninja fetch is in flight
    private bool _lowerPriceValueScanErrorLogged;

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
                                                    else if (orbType == "Mirror of Kalandra" && LowerPriceSettings.RepriceMirror.Value) reprice = true;

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
            case "Mirror of Kalandra":
                useFlatReduction = LowerPriceSettings.MirrorUseFlat ? true : LowerPriceSettings.UseFlatReduction;
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
            // Chaos is the unit of account, so it is known without the API. Every other
            // currency stays absent until poe.ninja answers; an absent entry means "unpriced",
            // which the display reports rather than silently counting as zero.
            _lowerPriceChaosValues.Clear();
            _lowerPriceChaosValues["Chaos Orb"] = 1m;
        }
    }

    private async Task UpdateLowerPriceCurrencyRates()
    {
        if (!LowerPriceSettings.AutoUpdateRates) return;

        var timeSinceUpdate = DateTime.Now - _lowerPriceLastCurrencyUpdate;
        if (timeSinceUpdate.TotalMinutes < LowerPriceSettings.CurrencyUpdateInterval.Value) return;

        // The render loop calls this every frame, so once the interval expires every frame in
        // flight passes the check above at once and fires its own request. poe.ninja asks callers
        // to be reasonable with concurrency, so let exactly one fetch run at a time.
        if (System.Threading.Interlocked.Exchange(ref _lowerPriceRatesFetching, 1) == 1) return;

        try
        {
            // Fetch currency rates from poe.ninja for the league the player is actually in.
            // This only powers the value display and cross-currency overrides — percentage
            // repricing works without it, so any failure here is non-fatal (keep default rates).
            JsonDocument jsonDoc = null;
            string league = ResolveLeague();
            // poe.ninja's legacy /api/data/currencyoverview and /api/data/itemoverview endpoints
            // were retired and now answer 404 for every league. The current economy API is
            // /poe1/api/economy/exchange/current/overview, documented at https://poe.ninja/docs/api.
            // Responses are HTTP-cached ~5 minutes, so CurrencyUpdateInterval must stay >= 5 (it is).
            string ninjaUrl = $"https://poe.ninja/poe1/api/economy/exchange/current/overview?league={Uri.EscapeDataString(league)}&type=Currency";
            try
            {
                using (var ninjaReq = new HttpRequestMessage(HttpMethod.Get, ninjaUrl))
                {
                    ninjaReq.Headers.Add("User-Agent", PluginUserAgent);
                    using (var ninjaResp = await _lowerPriceHttpClient.SendAsync(ninjaReq))
                    {
                        if (ninjaResp.IsSuccessStatusCode)
                        {
                            var response = await ninjaResp.Content.ReadAsStringAsync();
                            jsonDoc = JsonDocument.Parse(response);
                        }
                        else
                        {
                            LogMessage($"LowerPrice: poe.ninja returned HTTP {(int)ninjaResp.StatusCode} for league '{league}'. Currency value display is unavailable (percentage repricing is unaffected).");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"LowerPrice: could not reach poe.ninja ({ex.Message}). Currency value display unavailable; repricing still works.");
            }

            if (jsonDoc == null)
            {
                // Optional local fallback file (not shipped by default).
                var localJsonPath = Path.Combine(DirectoryFullName, "poeninja.json");
                if (File.Exists(localJsonPath))
                {
                    try
                    {
                        var localJson = await File.ReadAllTextAsync(localJsonPath);
                        jsonDoc = JsonDocument.Parse(localJson);
                    }
                    catch (Exception ex)
                    {
                        LogError($"LowerPrice: failed to parse local poeninja.json: {ex.Message}");
                    }
                }

                if (jsonDoc == null)
                    return; // Keep default rates; non-fatal.
            }
            
            // The economy API splits the data in two: "items" maps a currency id to its display
            // name ("annul" -> "Orb of Annulment"), "lines" maps that id to its chaos price
            // ("annul" -> 12.14). Joining them gives a name-keyed table that matches the orb
            // names read off the item tooltips verbatim, so every currency is priceable rather
            // than only the four that used to be hardcoded.
            var idToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (jsonDoc.RootElement.TryGetProperty("items", out var itemsEl) &&
                itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in itemsEl.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idEl) &&
                        item.TryGetProperty("name", out var nameEl))
                    {
                        var id = idEl.GetString();
                        var name = nameEl.GetString();
                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                            idToName[id] = name;
                    }
                }
            }

            var parsed = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            if (jsonDoc.RootElement.TryGetProperty("lines", out var lines) &&
                lines.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in lines.EnumerateArray())
                {
                    if (!line.TryGetProperty("id", out var idEl) ||
                        !line.TryGetProperty("primaryValue", out var valEl))
                        continue;

                    var id = idEl.GetString();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    if (!idToName.TryGetValue(id, out var name)) continue;
                    if (valEl.ValueKind != JsonValueKind.Number) continue;

                    var chaosValue = valEl.GetDecimal();
                    if (chaosValue > 0)
                        parsed[name] = chaosValue;
                }
            }

            if (parsed.Count == 0)
            {
                // poe.ninja answers 200 with an empty payload for a league it doesn't know, so an
                // empty result means a bad league name far more often than a dead market.
                LogMessage($"LowerPrice: poe.ninja returned no currency prices for league '{league}'. Check that the league name is correct; value totals will show as unavailable.");
                return;
            }

            lock (_lowerPriceCurrencyRatesLock)
            {
                _lowerPriceChaosValues = parsed;
                _lowerPriceChaosValues["Chaos Orb"] = 1m;
            }

            _lowerPriceLastCurrencyUpdate = DateTime.Now;
        }
        catch (Exception ex)
        {
            LogError($"Failed to update currency rates: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _lowerPriceRatesFetching, 0);
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

            var pos = new Vector2(LowerPriceSettings.ValueDisplayX.Value, LowerPriceSettings.ValueDisplayY.Value);
            var totalItemsInTab = items?.Count() ?? 0;

            // Prefer the last all-tabs scan for this tab. Reading prices out of the game means
            // reading hover tooltips, and the client doesn't build a tooltip until you actually
            // hover the item — so the in-game path can only ever see what you've already touched.
            // The API scan has every price whether or not you hovered anything.
            var scanned = GetScannedValueForOpenTab();
            var divineInChaos = GetLowerPriceChaosValue("Divine Orb");

            string displayText;
            if (scanned != null)
            {
                displayText = $"Items in tab: {scanned.ItemsPriced}/{totalItemsInTab}  (scanned)\n";

                foreach (var orb in scanned.OrbTotals
                             .OrderByDescending(o => GetLowerPriceChaosValue(o.Key) * o.Value)
                             .ThenBy(o => o.Key))
                {
                    displayText += $"{orb.Key}: {orb.Value:N0}\n";
                }

                displayText += $"\nTotal in Chaos: {scanned.ChaosTotal:N0}\n";
                displayText += divineInChaos > 0
                    ? $"Total in Divine: {scanned.ChaosTotal / divineInChaos:F2}"
                    : "Total in Divine: rates unavailable";

                if (scanned.UnpricedItems > 0)
                    displayText += $"\n({scanned.UnpricedItems} item(s) in an unpriced currency)";
            }
            else
            {
                var itemValues = CalculateLowerPriceItemValues(items);

                displayText = $"Items in tab: {itemValues.ItemsWithPricing}/{totalItemsInTab}\n";
                displayText += $"Items for sale: {itemValues.TotalItems}\n";

                // Breakdown, most valuable currency first.
                foreach (var orb in itemValues.OrbTotals
                             .OrderByDescending(o => GetLowerPriceChaosValue(o.Key) * o.Value)
                             .ThenBy(o => o.Key))
                {
                    displayText += $"{orb.Key}: {orb.Value:N0}\n";
                }

                displayText += $"\nTotal in Chaos: {itemValues.TotalInChaos:N0}\n";
                displayText += itemValues.DivineRateKnown
                    ? $"Total in Divine: {itemValues.TotalInDivine:F2}"
                    : "Total in Divine: rates unavailable";

                if (itemValues.UnpricedItems > 0)
                    displayText += $"\n({itemValues.UnpricedItems} item(s) in an unpriced currency)";

                // Only relevant on the tooltip path; a scan makes hovering unnecessary.
                if (items != null && items.Count(i => i.Tooltip != null) < totalItemsInTab)
                {
                    displayText += $"\n⚠️ Not scanned — hover items, or press " +
                                   $"{LowerPriceSettings.StashScanHotkey.Value} to scan all tabs.";
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

    // Takes the concrete element type rather than IEnumerable<dynamic> on purpose. With dynamic,
    // every member access below binds at runtime, and C# cannot resolve EXTENSION methods on a
    // dynamic receiver — so `child1.Children.Last()` threw RuntimeBinderException ("IList<Element>
    // does not contain a definition for 'Last'") for every single item. The per-item
    // catch swallowed it, so the panel silently reported 0 priced items no matter what was in the
    // tab. Statically typed, Last() resolves normally.
    private ItemValueSummary CalculateLowerPriceItemValues(IEnumerable<NormalInventoryItem> items)
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

                    // Per-currency breakdown, keyed by whatever orb the item is actually priced
                    // in. The old code only recognised four hardcoded orbs and dropped the rest.
                    summary.OrbTotals.TryGetValue(orbType, out var orbSoFar);
                    summary.OrbTotals[orbType] = orbSoFar + price;

                    var chaosEach = GetLowerPriceChaosValue(orbType);
                    if (chaosEach > 0)
                        summary.TotalInChaos += price * chaosEach;
                    else
                        summary.UnpricedItems++;
                }
                catch (Exception ex)
                {
                    // Skip this item, but say so once per session. Swallowing this silently is
                    // exactly how the RuntimeBinderException above went unnoticed: the panel just
                    // reported "0 items priced" forever with no error anywhere.
                    if (!_lowerPriceValueScanErrorLogged)
                    {
                        _lowerPriceValueScanErrorLogged = true;
                        LogError($"LowerPrice value display: failed to read a price from an item ({ex.GetType().Name}: {ex.Message}). The stash tooltip layout may have changed; totals will be incomplete.");
                    }
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"LowerPrice value display: item scan aborted ({ex.Message}).");
            return summary;
        }
        
        // Set the processing stats
        summary.TotalItemsProcessed = totalItemsProcessed;
        summary.ItemsWithTooltips = itemsWithTooltips;
        summary.ItemsWithPricing = itemsWithPricing;

        // Convert the chaos total once, at the end, rather than per item.
        var divineInChaos = GetLowerPriceChaosValue("Divine Orb");
        summary.DivineRateKnown = divineInChaos > 0;
        if (summary.DivineRateKnown)
            summary.TotalInDivine = summary.TotalInChaos / divineInChaos;

        return summary;
    }

    /// <summary>Chaos value of one unit of <paramref name="currencyName"/>, or 0 if unknown.</summary>
    private decimal GetLowerPriceChaosValue(string currencyName)
    {
        if (string.IsNullOrWhiteSpace(currencyName)) return 0m;
        lock (_lowerPriceCurrencyRatesLock)
        {
            return _lowerPriceChaosValues.TryGetValue(currencyName.Trim(), out var v) && v > 0 ? v : 0m;
        }
    }
}

public class ItemValueSummary
{
    public int TotalItems { get; set; }
    public int TotalItemsProcessed { get; set; }
    public int ItemsWithTooltips { get; set; }
    public int ItemsWithPricing { get; set; }

    /// <summary>Sum of asking prices per orb type, e.g. "Divine Orb" -> 12.</summary>
    public Dictionary<string, decimal> OrbTotals { get; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Items priced in a currency with no known chaos value, so absent from the totals.</summary>
    public int UnpricedItems { get; set; }

    public decimal TotalInChaos { get; set; }
    public decimal TotalInDivine { get; set; }

    /// <summary>False when the divine rate hasn't loaded, so TotalInDivine is meaningless.</summary>
    public bool DivineRateKnown { get; set; }
}

