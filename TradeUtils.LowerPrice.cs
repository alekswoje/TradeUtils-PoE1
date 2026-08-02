using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
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
    // Display names the client uses, on tooltips and in the currency dropdown.
    private const string ChaosOrbName = "Chaos Orb";
    private const string DivineOrbName = "Divine Orb";
    private const string MirrorOrbName = "Mirror of Kalandra";

    // Fixed values that used to be settings. They were knobs nobody needs to turn, and the menu had
    // grown to the point where the handful that do matter were hard to find.
    private const int LowerPriceTimerMinutes = 60;
    private const int LowerPriceRateRefreshMinutes = 30; // poe.ninja caches ~5 min, so stay well above it
    private const int LowerPriceValueDisplayX = 10;
    private const int LowerPriceValueDisplayY = 100;
    private const int LowerPriceStashValueDisplayX = 300;
    private const int LowerPriceStashValueDisplayY = 100;
    private const int LowerPriceStepDownMaxAmount = 10000;

    /// <summary>Press-to-release hold, and the settle inside a single gesture. A real click is
    /// tens of milliseconds, not the full inter-action delay.</summary>
    private const int LowerPriceClickHoldMs = 15;

    /// <summary>
    /// How often the UI waits re-check. Every wait in a step down - dialog open, list open, list
    /// close, dialog close - used to round up to the next 25ms tick, so this was costing more than
    /// the clicks themselves.
    /// </summary>
    private const int LowerPriceUiPollMs = 8;

    /// <summary>
    /// How long to wait for the Set Item Price dialog after right-clicking an item. This doubles as
    /// the locked-item test: the client paints a padlock on items it won't let you price, but that
    /// padlock has no element behind it - no texture, no distinct tooltip type, nothing readable -
    /// so "the dialog didn't open" is the only signal there is. Kept short so skipping a tab full
    /// of locked items costs a fraction of a second each rather than seconds.
    /// </summary>
    private const int LowerPriceDialogOpenTimeoutMs = 600;

    /// <summary>Humanised pause between UI actions, drawn fresh each time it's read.</summary>
    private int LowerPriceStepDelayMs =>
        ActionDelayWithJitterMs;

    // LowerPrice-specific fields
    private readonly ConcurrentDictionary<RectangleF, bool?> _lowerPriceMouseStateForRect = new();
    private readonly Random _lowerPriceRandom = new Random();
    private DateTime _lowerPriceLastRepriceTime = DateTime.MinValue;
    private bool _lowerPriceTimerExpired = false;
    private WaveOutEvent _lowerPriceWaveOut;
    private bool _lowerPriceManualRepriceTriggered = false;
    private int _lowerPriceRunning; // 0 = idle, 1 = a reprice run is in flight

    // Items a run left alone because a step down was due and couldn't be made. Plugin log messages
    // never reach the log files, so without these the run looks like it worked - which is exactly
    // how a disabled step down went unnoticed for three releases. Accumulated across an all-tabs
    // sweep, reset when a run starts, and rendered on the value display.
    private volatile int _lowerPriceRunStepDownBlocked;
    private volatile string _lowerPriceRunStepDownReason;
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

    // Which league the table in _lowerPriceChaosValues was actually fetched for. Without this the
    // rates get pinned to whatever league resolved on the very first fetch — which is the fallback,
    // since ServerData.League reads empty and the API lookup hasn't returned yet at startup — and
    // the interval guard then holds those wrong numbers for a full refresh period.
    private string _lowerPriceRatesLeague;

    // Backoff after a failed fetch, so a league poe.ninja doesn't know can't turn the render loop
    // into a request flood.
    private DateTime _lowerPriceRatesNextAttempt = DateTime.MinValue;
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
            if (LowerPriceSettings.EnableTimer.Value)
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

                // Second button, immediately to the right: same reprice, every tab.
                var allTabsRect = new RectangleF(buttonPos.X + buttonSize + 6, buttonPos.Y, buttonSize, buttonSize);

                // Third: value every tab. Read-only - it only hovers, never right-clicks.
                var valueScanRect = new RectangleF(buttonPos.X + 2 * (buttonSize + 6), buttonPos.Y, buttonSize, buttonSize);

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

                // The all-tabs button, drawn as a plain labelled box - there's no art for it.
                try
                {
                    Graphics.DrawBox(allTabsRect, new SharpDX.Color(60, 90, 160, 220));
                    Graphics.DrawFrame(allTabsRect, new SharpDX.Color(255, 255, 255, 255), 2);
                    Graphics.DrawText("ALL", new Vector2(allTabsRect.X + 5, allTabsRect.Y + 12),
                                      new SharpDX.Color(255, 255, 255, 255));
                }
                catch (Exception ex)
                {
                    LogError($"LowerPrice DEBUG: Failed to draw all-tabs button: {ex.Message}");
                }

                // Value-scan button, drawn green so it reads as the harmless one.
                try
                {
                    Graphics.DrawBox(valueScanRect, new SharpDX.Color(50, 130, 80, 220));
                    Graphics.DrawFrame(valueScanRect, new SharpDX.Color(255, 255, 255, 255), 2);
                    Graphics.DrawText("VAL", new Vector2(valueScanRect.X + 5, valueScanRect.Y + 12),
                                      new SharpDX.Color(255, 255, 255, 255));
                }
                catch (Exception ex)
                {
                    LogError($"LowerPrice DEBUG: Failed to draw value-scan button: {ex.Message}");
                }

                // Check for button press or manual trigger
                var buttonPressed = IsLowerPriceButtonPressed(buttonRect);
                var allTabsPressed = IsLowerPriceButtonPressed(allTabsRect);
                var valueScanPressed = IsLowerPriceButtonPressed(valueScanRect);

                if (buttonPressed || allTabsPressed || valueScanPressed || _lowerPriceManualRepriceTriggered)
                {
                    var sweepAllTabs = allTabsPressed;
                    var valueScan = valueScanPressed;
                    LogMessage($"LowerPrice DEBUG: Button pressed={buttonPressed}, AllTabs={allTabsPressed}, ManualTrigger={_lowerPriceManualRepriceTriggered}");
                    _lowerPriceManualRepriceTriggered = false; // Reset manual trigger

                    // One run at a time. Without this a second click part-way through starts a
                    // parallel pass that fights the first one for the mouse.
                    if (System.Threading.Interlocked.Exchange(ref _lowerPriceRunning, 1) == 1)
                    {
                        LogMessage("LowerPrice: a reprice run is already going; ignoring the click.");
                    }
                    else
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                while (Control.MouseButtons == MouseButtons.Left)
                                {
                                    await Task.Delay(10);
                                }

                                // Fresh counters per run, so the overlay reports this pass rather
                                // than something left over from the last one.
                                _lowerPriceRunStepDownBlocked = 0;
                                _lowerPriceRunStepDownReason = null;

                                if (valueScan)
                                    await ScanAllLowerPriceShopTabValues();
                                else if (sweepAllTabs)
                                    await RepriceAllLowerPriceTabs();
                                else
                                    await UpdateLowerPriceAllItemPrices(offlineMerchantPanel);
                            }
                            catch (Exception ex)
                            {
                                LogError($"LowerPrice: reprice run failed ({ex.Message}).");
                            }
                            finally
                            {
                                System.Threading.Interlocked.Exchange(ref _lowerPriceRunning, 0);
                            }
                        });
                    }
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

    /// <summary>
    /// Reprices every item in the tab that is currently open. Returns false when the run hit
    /// something that should stop an all-tabs sweep too (chat opened, a wrong-currency step down),
    /// as opposed to merely finding nothing to do.
    /// </summary>
    private async Task<bool> UpdateLowerPriceAllItemPrices(object offlineMerchantPanel)
    {
        try
        {
            LogMessage("=== LowerPrice: Starting reprice operation ===");
            
            // POE1: Use OfflineMerchantPanel for offline merchant panel
            var panel = GameController.IngameState.IngameUi.OfflineMerchantPanel;
            
            if (panel == null)
            {
                LogError("LowerPrice DEBUG: OfflineMerchantPanel is null");
                return false;
            }
            
            if (!panel.IsVisible)
            {
                LogError("LowerPrice DEBUG: OfflineMerchantPanel is not visible");
                return false;
            }
            
            // OfflineMerchantPanel is a StashElement, so we need to access VisibleStash first
            var visibleStash = panel.VisibleStash;
            if (visibleStash == null)
            {
                LogError("LowerPrice DEBUG: VisibleStash is null");
                return true;
            }
            
            var items = visibleStash.VisibleInventoryItems;
            
            if (items == null)
            {
                LogError("LowerPrice DEBUG: VisibleInventoryItems is null");
                return true;
            }
            
            var itemCount = items.Count();
            LogMessage($"LowerPrice DEBUG: Found {itemCount} items in merchant panel");
            
            if (!items.Any())
            {
                LogMessage("LowerPrice DEBUG: No items to process");
                return true;
            }

            int processedCount = 0;
            int skippedLocked = 0;
            int skippedNoPrice = 0;
            int repriced = 0;
            int pickedUp = 0;
            int steppedDown = 0;
            bool stepDownVerificationFailed = false;
            bool chatWasOpen = false;
            int unverifiedStepDowns = 0;
            int stepDownUnavailable = 0;
            string stepDownBlockedReason = null;

            // The dropdown row order can't change part-way through a run, so the first step down is
            // the only one worth checking. Checked, not proven - if the tooltip couldn't be read the
            // rest of the run simply goes unverified rather than paying the wait again each time.
            bool stepDownRowOrderChecked = false;
            bool structureDumped = false;  // Only dump structure once for first item
            
            foreach (var item in items)
            {
                try
                {
                    processedCount++;
                    
                    if (LowerPriceMoveCancellationRequested)
                    {
                        LogMessage("LowerPrice DEBUG: Breaking - right mouse button held (cancel)");
                        break;
                    }

                    // Hard stop: if the chat input is open, the next keystroke goes into a public
                    // channel instead of a price field. Close it and abandon the run.
                    if (IsLowerPriceChatOpen())
                    {
                        Utility.Keyboard.KeyPress(Keys.Escape);
                        chatWasOpen = true;
                        break;
                    }

                    // Committing a price dialog can blank the panel for a frame or two. Closing it
                    // for real is still the way to stop a run, so only give up once it stays gone.
                    if (!panel.IsVisible &&
                        !await WaitForLowerPriceCondition(() => panel.IsVisible, 750))
                    {
                        LogMessage("LowerPrice DEBUG: Breaking - merchant panel closed");
                        break;
                    }

                    if (item.Children?.Count == 2)
                    {
                        LogMessage($"LowerPrice DEBUG: Item {processedCount} - Skipping (has 2 children)");
                        await TaskUtils.NextFrame();
                        await Task.Delay(LowerPriceStepDelayMs);
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
                    await Task.Delay(LowerPriceStepDelayMs);

                    // The item is hovered by now, so its tooltip is up and carries the padlock notice
                    // if there is one. Reading it here costs nothing and skips straight to the next
                    // item - no right-click, no waiting on a dialog that was never going to open.
                    if (IsLowerPriceItemLocked(item))
                    {
                        LogMessage($"LowerPrice DEBUG: Item {processedCount} - Skipping (locked: priced too recently)");
                        skippedLocked++;
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

                                                    // A garbled read across a panel rebuild produces
                                                    // arbitrary bytes, not a failure. Repricing off
                                                    // one means acting on an item whose currency we
                                                    // don't actually know - and it would also make
                                                    // step down refuse, since no rate can match.
                                                    if (!IsPlausibleLowerPriceOrbName(orbType))
                                                    {
                                                        LogMessage($"LowerPrice DEBUG: Item {processedCount} - unreadable currency, skipping.");
                                                        skippedNoPrice++;
                                                        continue;
                                                    }

                                                    orbType = orbType.Trim();
                                                    // Everything priced in a currency we can read is repriced, except that
                                                    // Divine and Mirror listings each keep an opt-out — those are the ones
                                                    // where an automated mistake is worth the most.
                                                    bool reprice = orbType switch
                                                    {
                                                        DivineOrbName => LowerPriceSettings.RepriceDivine.Value,
                                                        MirrorOrbName => LowerPriceSettings.RepriceMirror.Value,
                                                        _ => !string.IsNullOrWhiteSpace(orbType),
                                                    };

                                                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - Reprice = {reprice}");
                                                    if (!reprice)
                                                    {
                                                        skippedNoPrice++;
                                                        continue;
                                                    }

                                                    float newPrice = CalculateLowerPriceNewPrice(oldPrice, orbType);
                                                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - Calculated newPrice = {newPrice}");

                                                    // Down here every further cut in the item's own currency is enormous, and at 1
                                                    // there is no cut left to make. Move the listing onto a cheaper currency so it
                                                    // can keep sliding in small steps instead.
                                                    if (LowerPriceSettings.StepDownCurrency.Value &&
                                                        oldPrice <= LowerPriceSettings.StepDownAtOrBelow.Value)
                                                    {
                                                        var stepPrice = CalculateLowerPriceStepDown(oldPrice, orbType, out var targetOrb,
                                                                                                    out var stepBlockedBy, out var stepRefusal);
                                                        if (stepPrice > 0)
                                                        {
                                                            LogMessage($"LowerPrice DEBUG: Item {processedCount} - Stepping down {oldPrice}x {orbType} to {stepPrice}x {targetOrb}");
                                                            var itemIsLocked = false;
                                                            if (await TryLowerPriceStepDownListing(stepPrice, targetOrb, position,
                                                                                                  () => itemIsLocked = true))
                                                            {
                                                                // The currency dropdown can't be read back, so the tooltip is the only
                                                                // check available - and it costs a full hover-rebuild wait. Since the
                                                                // row order is fixed for the length of a run, checking the first step
                                                                // down proves it for all of them; the rest skip straight through.
                                                                if (!stepDownRowOrderChecked)
                                                                {
                                                                    // Once per run, whatever the outcome. Retrying on every item is
                                                                    // what made this crawl: an unconfirmable tooltip meant each step
                                                                    // down paid the full wait again for a check that was never going
                                                                    // to pass.
                                                                    stepDownRowOrderChecked = true;

                                                                    var verdict = await VerifyLowerPriceStepDown(
                                                                        item, position, stepPrice, targetOrb, oldPrice, orbType);

                                                                    // Only a positively wrong currency means anything is broken. A
                                                                    // tooltip that won't rebuild in time proves nothing either way.
                                                                    if (verdict == LowerPriceStepDownVerdict.WrongCurrency)
                                                                    {
                                                                        stepDownVerificationFailed = true;
                                                                        break;
                                                                    }

                                                                    if (verdict != LowerPriceStepDownVerdict.Confirmed)
                                                                        unverifiedStepDowns++;
                                                                }

                                                                steppedDown++;
                                                                LogMessage($"LowerPrice DEBUG: Item {processedCount} - Relisted as {stepPrice}x {targetOrb}");

                                                                if (LowerPriceSettings.EnableTimer.Value)
                                                                {
                                                                    _lowerPriceLastRepriceTime = DateTime.Now;
                                                                    _lowerPriceTimerExpired = false;
                                                                }

                                                                // No trailing pause: the next iteration opens with its own move-and-
                                                                // settle before it touches anything, so waiting here just doubled it.
                                                                continue;
                                                            }

                                                            if (itemIsLocked)
                                                            {
                                                                LogMessage($"LowerPrice DEBUG: Item {processedCount} - locked, skipping.");
                                                                skippedLocked++;
                                                                continue;
                                                            }

                                                            // The listing is untouched, so the normal path below is still safe to run.
                                                            LogError($"LowerPrice: step down to {targetOrb} didn't go through for item {processedCount}; leaving it priced in {orbType}.");
                                                        }
                                                        else if (stepRefusal == LowerPriceStepDownRefusal.Unavailable)
                                                        {
                                                            // The listing is down where a cut in its own currency is enormous — 3 Divine
                                                            // to 2 is 33% — which is the exact situation step down exists to avoid. If
                                                            // the step can't be made, quietly taking that cut instead is worse than
                                                            // doing nothing, so leave the listing alone and report why on the overlay.
                                                            LogError($"LowerPrice: item {processedCount} ({oldPrice}x {orbType}) should have stepped down but couldn't: {stepBlockedBy}");
                                                            stepDownUnavailable++;
                                                            stepDownBlockedReason ??= stepBlockedBy;
                                                            continue;
                                                        }
                                                        else
                                                        {
                                                            LogMessage($"LowerPrice DEBUG: Item {processedCount} - Not stepping down: {stepBlockedBy}");
                                                        }
                                                    }

                                                    if (oldPrice == 1)
                                                    {
                                                        LogMessage($"LowerPrice DEBUG: Item {processedCount} - Price is 1, PickupItemsAtOne = {LowerPriceSettings.PickupItemsAtOne.Value}");
                                                        if (LowerPriceSettings.PickupItemsAtOne.Value)
                                                        {
                                                            LogMessage($"LowerPrice DEBUG: Item {processedCount} - Picking up item");
                                                            Utility.Keyboard.KeyDown(Keys.LControlKey);
                                                            await TaskUtils.NextFrame();
                                                            await Task.Delay(LowerPriceStepDelayMs);
                                                            Utility.Mouse.LeftDown();
                                                            await TaskUtils.NextFrame();
                                                            await Task.Delay(LowerPriceStepDelayMs);
                                                            Utility.Mouse.LeftUp();
                                                            await TaskUtils.NextFrame();
                                                            await Task.Delay(LowerPriceStepDelayMs);
                                                            Utility.Keyboard.KeyUp(Keys.LControlKey);
                                                            await TaskUtils.NextFrame();
                                                            await Task.Delay(LowerPriceStepDelayMs);
                                                            pickedUp++;
                                                        }
                                                        continue;
                                                    }

                                                    if (newPrice < 1) newPrice = 1;
                                                    LogMessage($"LowerPrice DEBUG: Item {processedCount} - Repricing from {oldPrice} to {newPrice}");
                                                    Utility.Mouse.RightDown();
                                                    await LowerPriceInputDelay();
                                                    Utility.Mouse.RightUp();

                                                    // Never type without the price dialog in front of it. This used to fire blind:
                                                    // one right-click that didn't land meant the digits went nowhere and Enter
                                                    // opened chat, after which every remaining item typed its price into chat and
                                                    // Enter sent it. A hundred-item tab became a hundred public messages.
                                                    if (await WaitForLowerPriceDialog(LowerPriceDialogOpenTimeoutMs) == null)
                                                    {
                                                        LogMessage($"LowerPrice DEBUG: Item {processedCount} - price dialog didn't open (locked); skipping.");
                                                        skippedLocked++;
                                                        await LowerPriceActionStep();
                                                        continue;
                                                    }

                                                    Utility.Keyboard.Type($"{newPrice}");
                                                    await LowerPriceInputDelay();

                                                    // Enter is only safe while the dialog still owns the keyboard.
                                                    if (GetLowerPriceDialog() == null)
                                                    {
                                                        LogError($"LowerPrice: item {processedCount} - the price dialog closed mid-edit; not pressing Enter.");
                                                        skippedNoPrice++;
                                                        await LowerPriceActionStep();
                                                        continue;
                                                    }

                                                    Utility.Keyboard.KeyPress(Keys.Enter);
                                                    await LowerPriceActionStep();
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
                    await Task.Delay(LowerPriceStepDelayMs);
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
            LogMessage($"LowerPrice DEBUG: Items stepped down a currency: {steppedDown}");
            LogMessage($"LowerPrice DEBUG: Items picked up: {pickedUp}");

            // Hand the blocked count to the overlay. Added rather than assigned: an all-tabs sweep
            // calls this once per tab, and the total across the sweep is what matters.
            if (stepDownUnavailable > 0)
            {
                _lowerPriceRunStepDownBlocked += stepDownUnavailable;
                _lowerPriceRunStepDownReason ??= stepDownBlockedReason;
                LogError($"LowerPrice: {stepDownUnavailable} item(s) left untouched - step down was due but " +
                         $"unavailable: {stepDownBlockedReason}");
            }

            if (unverifiedStepDowns > 0)
                LogMessage("LowerPrice: couldn't read the first step down back off the item's tooltip, so this " +
                           "run's step downs went unverified. They were relisted; the currency just wasn't " +
                           "confirmed. Spot-check one.");

            if (chatWasOpen)
                LogError("=== LowerPrice: run STOPPED because the chat input was open. Nothing was typed into it. " +
                         "Chat was closed for you; check the last item's price and re-run. ===");

            if (stepDownVerificationFailed)
                LogError("=== LowerPrice: run STOPPED early because a step down landed on the WRONG CURRENCY. " +
                         "The last item touched is mispriced — fix it, and fix LowerPriceCurrencyRowIndex, " +
                         "before running again. ===");

            // Both of these mean the next tab would go the same way, so an all-tabs sweep must stop
            // rather than repeat the mistake 20 more times.
            return !chatWasOpen && !stepDownVerificationFailed;
        }
        catch (Exception ex)
        {
            // Log error for the entire reprice operation
            LogError($"LowerPrice DEBUG: Error in UpdateAllItemPrices: {ex.Message}\nStackTrace: {ex.StackTrace}");
            return false;
        }
    }

    // ===== Shop tabs =====
    //
    // The merchant's shop tabs are NOT what ExileCore calls the panel's stashes. OfflineMerchantPanel
    // reports exactly two "stashes" - Shop and Earnings (Remove-only) - while the tabs you actually
    // list items in live inside the Shop view as a plain element strip that ExileCore doesn't model.
    // Everything below drives that strip directly, off a path verified against the live client.
    //
    // OfflineMerchantPanel -> [2][0][0][1][1][0][0][1] is the tab bar, whose children are:
    //   [1] one container per shop tab; exactly one is IsVisibleLocal - that's the selected tab
    //   [2] the dropdown toggle button
    //   [4] the dropdown list, whose [2] holds one row per tab in the same order as [1]
    //   [6] / [7] the left / right scroll arrows
    //
    // Selection goes through the dropdown rather than the strip: the strip is a 737px viewport over
    // a ~2500px row, so most tabs sit outside it and clicking their reported rect would land on
    // whatever is drawn there instead.
    private static readonly int[] LowerPriceShopTabBarPath = { 2, 0, 0, 1, 1, 0, 0, 1 };
    private const int LowerPriceShopGridsIndex = 1;
    private const int LowerPriceShopDropdownButtonIndex = 2;
    private const int LowerPriceShopDropdownListIndex = 4;
    private const int LowerPriceShopDropdownRowsIndex = 2;

    /// <summary>Walks a chain of child indices, returning null the moment one doesn't exist.</summary>
    private static Element NavigateLowerPriceChildren(Element root, params int[] path)
    {
        var element = root;
        foreach (var index in path)
        {
            if (element == null) return null;
            IList<Element> children;
            try { children = element.Children; } catch { return null; }
            if (children == null || index < 0 || index >= children.Count) return null;
            element = children[index];
        }

        return element;
    }

    private Element GetLowerPriceShopGrids()
    {
        var panel = GameController?.IngameState?.IngameUi?.OfflineMerchantPanel;
        if (panel?.IsVisible != true) return null;

        var tabBar = NavigateLowerPriceChildren(panel, LowerPriceShopTabBarPath);
        return NavigateLowerPriceChildren(tabBar, LowerPriceShopGridsIndex);
    }

    /// <summary>Index of the shop tab on screen, or -1. Exactly one tab container is ever visible.</summary>
    private static int CurrentLowerPriceShopTab(Element grids)
    {
        if (grids == null) return -1;
        try
        {
            for (var i = 0; i < grids.ChildCount; i++)
                if (grids.Children[i].IsVisibleLocal) return i;
        }
        catch { }

        return -1;
    }

    /// <summary>Tab label, read off the dropdown row so it matches what's on screen.</summary>
    private string LowerPriceShopTabName(int index)
    {
        var panel = GameController?.IngameState?.IngameUi?.OfflineMerchantPanel;
        var tabBar = NavigateLowerPriceChildren(panel, LowerPriceShopTabBarPath);
        var label = NavigateLowerPriceChildren(tabBar, LowerPriceShopDropdownListIndex,
                                               LowerPriceShopDropdownRowsIndex, index, 0, 1);
        try
        {
            var text = label?.TextNoTags;
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        catch { }

        return $"tab {index + 1}";
    }

    private async Task<bool> SelectLowerPriceShopTab(int index)
    {
        var panel = GameController?.IngameState?.IngameUi?.OfflineMerchantPanel;
        var tabBar = NavigateLowerPriceChildren(panel, LowerPriceShopTabBarPath);
        var grids = NavigateLowerPriceChildren(tabBar, LowerPriceShopGridsIndex);
        if (tabBar == null || grids == null) return false;

        if (CurrentLowerPriceShopTab(grids) == index) return true;

        var list = NavigateLowerPriceChildren(tabBar, LowerPriceShopDropdownListIndex);
        if (list?.IsVisibleLocal != true)
        {
            var toggle = NavigateLowerPriceChildren(tabBar, LowerPriceShopDropdownButtonIndex);
            if (toggle == null) return false;

            await LowerPriceClickElement(toggle.GetClientRectCache);
            if (!await WaitForLowerPriceCondition(
                    () => NavigateLowerPriceChildren(tabBar, LowerPriceShopDropdownListIndex)?.IsVisibleLocal == true,
                    1500))
            {
                LogError("LowerPrice: the shop tab dropdown didn't open.");
                return false;
            }
        }

        var row = NavigateLowerPriceChildren(tabBar, LowerPriceShopDropdownListIndex,
                                             LowerPriceShopDropdownRowsIndex, index);
        if (row == null) return false;

        var rect = row.GetClientRectCache;
        if (rect.Width <= 0 || rect.Height <= 0) return false;

        await LowerPriceClickElement(rect);

        // The tab is only usable once its container is the visible one AND the panel is reporting
        // that tab's items - reading straight after the click returns the previous tab's contents.
        if (!await WaitForLowerPriceCondition(() => CurrentLowerPriceShopTab(grids) == index, 3000))
            return false;

        // Waiting for the list to be non-null was not enough: it comes back non-null but EMPTY for
        // a moment after the container flips, so a caller reading right here saw zero items and
        // moved on, silently dropping a whole tab. Wait for a count that is actually populated and
        // holding steady instead.
        await WaitForLowerPriceTabItems();
        return true;
    }

    /// <summary>
    /// Waits for the freshly-selected tab's item list to settle, and returns the count it settled
    /// on. The container flipping to the new tab and the panel reporting that tab's items are two
    /// different moments; reading in between gives an empty list that looks exactly like an empty
    /// tab.
    ///
    /// A populated tab returns the moment its count holds steady, so it costs a settle window and
    /// nothing more. An empty tab has nothing to wait for and can only be timed out on, so it gets
    /// its own much shorter budget - the full timeout is reserved for a list that is still visibly
    /// changing. Paying seconds per empty tab across two dozen of them is worse than the bug this
    /// was added to fix.
    /// </summary>
    private async Task<int> WaitForLowerPriceTabItems(int settleMs = 40, int emptyTimeoutMs = 350,
                                                      int timeoutMs = 1500)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var lastCount = -1;
        var steadySince = 0L;

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            var count = CurrentLowerPriceTabItems()?.Count ?? 0;

            if (count != lastCount)
            {
                // Still filling in - restart the settle window.
                lastCount = count;
                steadySince = stopwatch.ElapsedMilliseconds;
            }
            else if (count > 0 && stopwatch.ElapsedMilliseconds - steadySince >= settleMs)
            {
                return count;
            }
            else if (count == 0 && stopwatch.ElapsedMilliseconds >= emptyTimeoutMs)
            {
                // Nothing has appeared and nothing is changing. Waiting longer only helps if the
                // panel is unusually slow, and that costs every empty tab in the shop.
                return 0;
            }

            await Task.Delay(LowerPriceUiPollMs);
        }

        // Zero here means the tab really does look empty. It can't be distinguished from a tab that
        // never loaded, which is why callers report the count rather than treating it as fact.
        return lastCount < 0 ? 0 : lastCount;
    }

    /// <summary>
    /// Walks every shop tab and reprices each one, then returns to the tab it started on.
    /// </summary>
    private async Task RepriceAllLowerPriceTabs()
    {
        var panel = GameController?.IngameState?.IngameUi?.OfflineMerchantPanel;
        if (panel?.IsVisible != true)
        {
            LogError("LowerPrice: the merchant panel isn't open, so there are no tabs to sweep.");
            return;
        }

        var grids = GetLowerPriceShopGrids();
        var tabCount = grids == null ? 0 : (int)grids.ChildCount;
        if (tabCount == 0)
        {
            LogError("LowerPrice: couldn't find the shop tab strip. The merchant UI layout has changed - " +
                     "LowerPriceShopTabBarPath needs updating.");
            return;
        }

        var startingTab = CurrentLowerPriceShopTab(grids);
        LogMessage($"=== LowerPrice: repricing all {tabCount} shop tabs ===");

        var swept = 0;
        var unreachable = 0;

        for (var tab = 0; tab < tabCount; tab++)
        {
            if (LowerPriceMoveCancellationRequested)
            {
                LogMessage("LowerPrice: all-tabs sweep cancelled (right mouse button).");
                break;
            }

            if (IsLowerPriceChatOpen())
            {
                Utility.Keyboard.KeyPress(Keys.Escape);
                LogError("=== LowerPrice: all-tabs sweep STOPPED because the chat input was open. ===");
                break;
            }

            if (GameController?.IngameState?.IngameUi?.OfflineMerchantPanel?.IsVisible != true)
            {
                LogMessage("LowerPrice: merchant panel closed, ending the sweep.");
                break;
            }

            if (!await SelectLowerPriceShopTab(tab))
            {
                unreachable++;
                LogError($"LowerPrice: couldn't switch to shop tab {tab + 1}/{tabCount}; skipping it.");
                continue;
            }

            var tabName = LowerPriceShopTabName(tab);
            LogMessage($"--- LowerPrice: shop tab {tab + 1}/{tabCount} ({tabName}) ---");

            swept++;
            if (!await UpdateLowerPriceAllItemPrices(null))
            {
                LogError($"=== LowerPrice: all-tabs sweep STOPPED at shop tab {tab + 1}/{tabCount} ({tabName}). ===");
                break;
            }
        }

        // Put the panel back on the tab it was found on.
        if (startingTab >= 0) await SelectLowerPriceShopTab(startingTab);

        LogMessage($"=== LowerPrice: all-tabs sweep finished - {swept} tab(s) repriced" +
                   (unreachable > 0 ? $", {unreachable} unreachable" : "") + " ===");
    }

    // ===== Valuing every shop tab =====
    //
    // Item prices only exist on hover: the client doesn't build a tooltip until the cursor is on
    // the item, so there is no way to read a tab's worth of prices without walking the mouse over
    // it. This is that walk, across every shop tab, and it is strictly read-only - it never
    // right-clicks and never opens the price dialog, so it cannot change a listing.

    /// <summary>Totals from the last full shop scan. Swapped in whole, never mutated in place,
    /// because the render thread reads it while the scan is running.</summary>
    private sealed class LowerPriceShopScan
    {
        public DateTime CompletedAt;
        public bool Cancelled;
        public int TabsScanned;
        public int TabsTotal;
        public int ItemsPriced;
        public int ItemsSeen;
        public int UnpricedItems;
        public int MissedItems;     // hovered but never produced a price
        public int UnstableTabs;    // rebuilt under us repeatedly; totals may be short
        public int EmptyTabs;       // reported no items at all, even after waiting for them
        public decimal ChaosTotal;
        public string RatesLeague;
        public Dictionary<string, decimal> OrbTotals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        public List<KeyValuePair<string, decimal>> PerTab = new List<KeyValuePair<string, decimal>>();
    }

    private volatile LowerPriceShopScan _lowerPriceShopScan;
    private volatile string _lowerPriceShopScanProgress;

    /// <summary>Live item list for the open shop tab, re-read rather than cached.</summary>
    private IList<NormalInventoryItem> CurrentLowerPriceTabItems()
    {
        try
        {
            return GameController?.IngameState?.IngameUi?.OfflineMerchantPanel?.VisibleStash?.VisibleInventoryItems;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Hovers one item and waits for its tooltip to carry a price.</summary>
    private async Task<(bool Read, int Price, string Orb)> HoverReadLowerPricePrice(
        NormalInventoryItem item, Vector2 windowTopLeft, int timeoutMs)
    {
        var itemRect = item.GetClientRectCache;
        if (itemRect.Width <= 0 || itemRect.Height <= 0) return (false, 0, null);

        Utility.Mouse.moveMouse(new Vector2(windowTopLeft.X + itemRect.TopLeft.X + 5,
                                            windowTopLeft.Y + itemRect.TopLeft.Y + 5));

        // Poll rather than sleep a fixed amount: most tooltips are up within a frame or two, and a
        // whole shop is a lot of items to pay a worst-case wait on.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (TryReadLowerPriceTooltipPrice(item.Tooltip, out var price, out var orbType))
                return (true, price, orbType);

            await Task.Delay(LowerPriceUiPollMs);
        }

        return (false, 0, null);
    }

    /// <summary>
    /// Values the open tab. Everything here exists because the panel rebuilds its item list out
    /// from under you - a lock expiring, or an item selling, is enough - and every reference taken
    /// before that rebuild then points at an address with no tooltip behind it. Reading those
    /// silently yields nothing, so items just vanish from the total.
    ///
    /// So: the list is re-read on every single item rather than enumerated once, a changed item
    /// count abandons the attempt and restarts the tab from scratch, and anything that didn't
    /// produce a price gets a second pass with a longer wait before it's given up on.
    /// </summary>
    private async Task<bool> ScanCurrentLowerPriceTabValues(LowerPriceShopScan scan, string tabName)
    {
        const int maxAttempts = 3;

        // GetWindowRectangleTimeCache.TopLeft is a SharpDX vector; the rest of this file works in
        // System.Numerics, so convert once here rather than at each call.
        var window = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
        var windowTopLeft = new Vector2(window.X, window.Y);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            // No re-wait here: SelectLowerPriceShopTab has already settled this tab's list, and
            // waiting a second time doubled the cost of every empty tab for nothing. On a retry pass
            // the tab is long since loaded, so a zero now is real.
            var startCount = CurrentLowerPriceTabItems()?.Count ?? 0;
            if (startCount == 0)
            {
                scan.EmptyTabs++;
                LogMessage($"LowerPrice: '{tabName}' reported no items after waiting for them.");
                scan.PerTab.Add(new KeyValuePair<string, decimal>(tabName, 0m));
                return true;
            }

            // Tallied locally so a restart discards a half-finished pass instead of double-counting.
            var orbTotals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var tabChaos = 0m;
            var priced = 0;
            var unpriced = 0;
            var missed = new List<int>();
            var rebuilt = false;

            for (var i = 0; i < startCount; i++)
            {
                if (LowerPriceMoveCancellationRequested)
                {
                    scan.Cancelled = true;
                    return false;
                }

                var list = CurrentLowerPriceTabItems();
                if (list == null || list.Count != startCount)
                {
                    rebuilt = true;
                    break;
                }

                var result = await HoverReadLowerPricePrice(list[i], windowTopLeft, 300);
                if (!result.Read)
                {
                    missed.Add(i);
                    continue;
                }

                priced++;
                var orb = result.Orb.Trim();
                orbTotals.TryGetValue(orb, out var soFar);
                orbTotals[orb] = soFar + result.Price;

                var chaosEach = GetLowerPriceChaosValue(orb);
                if (chaosEach > 0) tabChaos += result.Price * chaosEach;
                else unpriced++;
            }

            if (rebuilt && attempt < maxAttempts)
            {
                LogMessage($"LowerPrice: '{tabName}' changed while being valued (an item locked or sold); rescanning it.");
                await Task.Delay(250);
                continue;
            }

            // Second pass over anything that didn't answer, with a longer wait. A miss is usually
            // just a tooltip that hadn't built yet, not an item without a price.
            if (missed.Count > 0 && !rebuilt)
            {
                foreach (var index in missed.ToArray())
                {
                    if (LowerPriceMoveCancellationRequested)
                    {
                        scan.Cancelled = true;
                        return false;
                    }

                    var list = CurrentLowerPriceTabItems();
                    if (list == null || list.Count != startCount || index >= list.Count) break;

                    var retry = await HoverReadLowerPricePrice(list[index], windowTopLeft, 700);
                    if (!retry.Read) continue;

                    missed.Remove(index);
                    priced++;
                    var orb = retry.Orb.Trim();
                    orbTotals.TryGetValue(orb, out var soFar);
                    orbTotals[orb] = soFar + retry.Price;

                    var chaosEach = GetLowerPriceChaosValue(orb);
                    if (chaosEach > 0) tabChaos += retry.Price * chaosEach;
                    else unpriced++;
                }
            }

            // Commit this attempt.
            scan.ItemsSeen += startCount;
            scan.ItemsPriced += priced;
            scan.UnpricedItems += unpriced;
            scan.MissedItems += missed.Count;
            scan.ChaosTotal += tabChaos;
            foreach (var kv in orbTotals)
            {
                scan.OrbTotals.TryGetValue(kv.Key, out var soFar);
                scan.OrbTotals[kv.Key] = soFar + kv.Value;
            }

            scan.PerTab.Add(new KeyValuePair<string, decimal>(tabName, tabChaos));

            if (rebuilt)
            {
                scan.UnstableTabs++;
                LogError($"LowerPrice: '{tabName}' kept changing while being valued; its total may be incomplete.");
            }
            else if (missed.Count > 0)
            {
                LogError($"LowerPrice: '{tabName}' - {missed.Count} item(s) never showed a price and aren't in the total.");
            }

            LogMessage($"LowerPrice: tab '{tabName}' = {tabChaos:N0} chaos ({priced}/{startCount} priced)");
            return true;
        }

        return true;
    }

    private async Task ScanAllLowerPriceShopTabValues()
    {
        var panel = GameController?.IngameState?.IngameUi?.OfflineMerchantPanel;
        if (panel?.IsVisible != true)
        {
            LogError("LowerPrice: the merchant panel isn't open, so there's nothing to value.");
            return;
        }

        var grids = GetLowerPriceShopGrids();
        var tabCount = grids == null ? 0 : (int)grids.ChildCount;
        if (tabCount == 0)
        {
            LogError("LowerPrice: couldn't find the shop tab strip, so only the open tab can be valued.");
            return;
        }

        var startingTab = CurrentLowerPriceShopTab(grids);
        var scan = new LowerPriceShopScan { TabsTotal = tabCount, RatesLeague = _lowerPriceRatesLeague };

        LogMessage($"=== LowerPrice: valuing all {tabCount} shop tabs ===");

        for (var tab = 0; tab < tabCount; tab++)
        {
            if (LowerPriceMoveCancellationRequested)
            {
                scan.Cancelled = true;
                LogMessage("LowerPrice: value scan cancelled (right mouse button).");
                break;
            }

            if (GameController?.IngameState?.IngameUi?.OfflineMerchantPanel?.IsVisible != true)
            {
                scan.Cancelled = true;
                LogMessage("LowerPrice: merchant panel closed, ending the value scan.");
                break;
            }

            _lowerPriceShopScanProgress = $"scanning tab {tab + 1}/{tabCount}...";

            if (!await SelectLowerPriceShopTab(tab))
            {
                LogError($"LowerPrice: couldn't switch to shop tab {tab + 1}/{tabCount} while valuing; skipping it.");
                continue;
            }

            var tabName = LowerPriceShopTabName(tab);

            if (!await ScanCurrentLowerPriceTabValues(scan, tabName)) break;
            scan.TabsScanned++;

            if (scan.Cancelled) break;
        }

        if (startingTab >= 0) await SelectLowerPriceShopTab(startingTab);

        scan.CompletedAt = DateTime.Now;
        _lowerPriceShopScan = scan;
        _lowerPriceShopScanProgress = null;

        LogMessage($"=== LowerPrice: value scan finished - {scan.TabsScanned}/{tabCount} tabs, " +
                   $"{scan.ItemsPriced} priced items, {scan.ChaosTotal:N0} chaos total ===");
    }

    /// <summary>The all-tabs totals block appended under the current tab's figures.</summary>
    private string LowerPriceShopScanSummary()
    {
        var progress = _lowerPriceShopScanProgress;
        if (progress != null) return $"\n\nAll tabs: {progress}";

        var scan = _lowerPriceShopScan;
        if (scan == null) return "";

        var age = DateTime.Now - scan.CompletedAt;
        var when = age.TotalMinutes < 1 ? "just now" : $"{age.TotalMinutes:F0}m ago";

        var text = $"\n\n=== ALL TABS ({scan.TabsScanned}/{scan.TabsTotal}) - {when} ===\n";
        text += $"Items priced: {scan.ItemsPriced}/{scan.ItemsSeen}\n";

        foreach (var orb in scan.OrbTotals
                     .OrderByDescending(o => GetLowerPriceChaosValue(o.Key) * o.Value)
                     .ThenBy(o => o.Key))
        {
            text += $"{orb.Key}: {orb.Value:N0}\n";
        }

        text += $"Total in Chaos: {scan.ChaosTotal:N0}\n";

        var divine = GetLowerPriceChaosValue(DivineOrbName);
        text += divine > 0
            ? $"Total in Divine: {scan.ChaosTotal / divine:F2}"
            : "Total in Divine: rates unavailable";

        if (scan.UnpricedItems > 0)
            text += $"\n({scan.UnpricedItems} item(s) in an unpriced currency)";

        // A silently-short total is worse than no total, so say when items were missed.
        if (scan.MissedItems > 0)
            text += $"\n⚠️ {scan.MissedItems} item(s) never showed a price - NOT counted";

        if (scan.UnstableTabs > 0)
            text += $"\n⚠️ {scan.UnstableTabs} tab(s) kept changing mid-scan - may be short";

        // Stated rather than assumed: some shops genuinely have empty tabs, but this is also what a
        // tab that never finished loading looks like, and the two are indistinguishable from here.
        if (scan.EmptyTabs > 0)
            text += $"\n({scan.EmptyTabs} tab(s) had no items)";

        if (scan.Cancelled)
            text += "\n⚠️ scan was cancelled - totals are partial";

        // Rates can move between the scan and now, and the totals were computed against the old ones.
        if (!string.IsNullOrWhiteSpace(scan.RatesLeague) &&
            !string.Equals(scan.RatesLeague, _lowerPriceRatesLeague, StringComparison.OrdinalIgnoreCase))
            text += $"\n⚠️ scanned against {scan.RatesLeague} rates";

        return text;
    }

    /// <summary>
    /// Whether <paramref name="orbType"/> steps down by the flat amount rather than the ratio.
    ///
    /// Divine and Mirror can opt in individually, because a percentage of a high-value listing is a
    /// huge move — 10% off 38 Divine is nearly 4 Divine in a single run — while a flat 1 eases it
    /// down. Everything else follows the global setting.
    ///
    /// The old build had five of these and three were named *UseRatio while forcing flat, so with
    /// stock settings a Chaos listing dropped by a flat 1 while the Price Ratio slider sat there
    /// looking like it was in charge. These two say what they do.
    /// </summary>
    private bool UsesFlatLowerPriceReduction(string orbType)
    {
        var orb = orbType?.Trim();

        if (string.Equals(orb, DivineOrbName, StringComparison.OrdinalIgnoreCase))
            return LowerPriceSettings.DivineUseFlat.Value || LowerPriceSettings.UseFlatReduction.Value;

        if (string.Equals(orb, MirrorOrbName, StringComparison.OrdinalIgnoreCase))
            return LowerPriceSettings.MirrorUseFlat.Value || LowerPriceSettings.UseFlatReduction.Value;

        return LowerPriceSettings.UseFlatReduction.Value;
    }

    /// <summary>The reduced price for a listing, by whichever strategy applies to its currency.</summary>
    private float CalculateLowerPriceNewPrice(int oldPrice, string orbType)
    {
        return UsesFlatLowerPriceReduction(orbType)
            ? oldPrice - LowerPriceSettings.FlatReductionAmount.Value
            : (float)Math.Floor(oldPrice * LowerPriceSettings.PriceRatio.Value);
    }

    /// <summary>
    /// The price tiers a listing walks down. Only the rungs people actually price in — stepping a
    /// Divine listing onto Exalts or Annuls would technically work but nobody shops that way.
    /// The order is taken from the live poe.ninja values rather than from this array, so the ladder
    /// follows the market if the tiers ever reorder.
    /// </summary>
    private static readonly string[] LowerPriceCurrencyLadder =
    {
        MirrorOrbName,
        DivineOrbName,
        ChaosOrbName,
    };

    /// <summary>
    /// The next rung below <paramref name="orbType"/> — the most valuable ladder currency that is
    /// still strictly cheaper than what the item is priced in now. Null when nothing is cheaper,
    /// which is the case for Chaos itself and for anything already below it.
    /// </summary>
    private string GetNextCheaperLowerPriceCurrency(string orbType)
    {
        var currentValue = GetLowerPriceChaosValue(orbType);
        if (currentValue <= 0) return null;

        // A currency off the ladder entirely (Exalted, Annul, ...) lands on the first rung below
        // its own value, which is Chaos for anything cheap. That is the sensible destination.
        return LowerPriceCurrencyLadder
            .Select(name => new { Name = name, Value = GetLowerPriceChaosValue(name) })
            .Where(c => c.Value > 0 && c.Value < currentValue)
            .OrderByDescending(c => c.Value)
            .Select(c => c.Name)
            .FirstOrDefault();
    }

    /// <summary>Why a step down didn't produce a price. The distinction drives what happens next.</summary>
    private enum LowerPriceStepDownRefusal
    {
        /// <summary>A step down is available.</summary>
        None,

        /// <summary>Nothing is wrong — the listing is already in the cheapest ladder currency.</summary>
        AlreadyCheapest,

        /// <summary>The step down should have happened and couldn't: rates, league, or the cap.</summary>
        Unavailable,
    }

    /// <summary>
    /// Works out the replacement listing when <paramref name="oldPrice"/>x <paramref name="orbType"/>
    /// is too low to keep cutting in its own currency: the equivalent amount of the next cheaper
    /// currency, with that currency's normal reduction already applied. 1 Divine at 200c comes out
    /// as 180 Chaos under a 0.9 ratio. Returns 0 when the step shouldn't happen, with
    /// <paramref name="reason"/> saying why and <paramref name="refusal"/> saying whether that's
    /// routine or a failure the caller has to react to.
    /// </summary>
    private int CalculateLowerPriceStepDown(int oldPrice, string orbType, out string targetOrb,
                                            out string reason, out LowerPriceStepDownRefusal refusal)
    {
        targetOrb = null;
        reason = null;
        refusal = LowerPriceStepDownRefusal.Unavailable;

        if (string.IsNullOrWhiteSpace(orbType))
        {
            reason = "the listing currency couldn't be read";
            return 0;
        }

        orbType = orbType.Trim();

        // The bottom of the ladder is settled without consulting the rate table at all, so a Chaos
        // listing still reduces normally when poe.ninja is unreachable. The table starts empty, and
        // without this every lookup below would return 0 and report a blocked step down for items
        // that were never going to step anywhere.
        if (string.Equals(orbType, LowerPriceCurrencyLadder[LowerPriceCurrencyLadder.Length - 1],
                          StringComparison.OrdinalIgnoreCase))
        {
            refusal = LowerPriceStepDownRefusal.AlreadyCheapest;
            reason = $"'{orbType}' is the cheapest rung on the ladder";
            return 0;
        }

        var sourceChaos = GetLowerPriceChaosValue(orbType);
        if (sourceChaos <= 0)
        {
            reason = $"there is no poe.ninja chaos rate for '{orbType}'";
            return 0;
        }

        // Which rung the listing sits on is settled before the league gate below, because the
        // ladder's ORDER is the same in every league — a Mirror outprices a Divine outprices a
        // Chaos wherever you play. Only the conversion needs the right league's numbers. Deciding
        // this first is what stops an unconfirmed league from making Chaos listings, which have
        // nowhere cheaper to go and were never stepping down, look like blocked step downs.
        targetOrb = GetNextCheaperLowerPriceCurrency(orbType);
        if (targetOrb == null)
        {
            // Routine, not a failure: a Chaos listing should just take its normal reduction. Every
            // other refusal here means the step down was meant to fire and didn't.
            refusal = LowerPriceStepDownRefusal.AlreadyCheapest;
            reason = $"'{orbType}' is already the cheapest rung on the ladder";
            return 0;
        }

        var targetChaos = GetLowerPriceChaosValue(targetOrb);
        if (targetChaos <= 0)
        {
            reason = $"there is no poe.ninja chaos rate for '{targetOrb}'";
            return 0;
        }

        // Rates drive the whole conversion, so a table that never loaded — or one left over from
        // hours ago, or belonging to another league — would misprice a real listing. Refuse rather
        // than guess: Standard puts a Divine near 829c against roughly 174c in the challenge league.
        if (!IsLowerPriceRateTableFresh(out var staleReason))
        {
            reason = staleReason;
            return 0;
        }

        var equivalent = oldPrice * sourceChaos / targetChaos;

        // Reduce by whatever rule the TARGET currency uses, so a stepped-down listing and one that
        // was always priced in that currency move by the same amount from here on. A Mirror stepping
        // onto Divine therefore picks up the Divine rule, not the Mirror one.
        var reduced = UsesFlatLowerPriceReduction(targetOrb)
            ? equivalent - LowerPriceSettings.FlatReductionAmount.Value
            : Math.Floor(equivalent * (decimal)LowerPriceSettings.PriceRatio.Value);

        var newPrice = (int)Math.Floor(reduced);
        if (newPrice < 1) newPrice = 1;

        // The clamp above can round a very cheap listing back up, and the point of a reprice is to
        // go down. Anything that doesn't is a no-op at best.
        if (newPrice * targetChaos >= oldPrice * sourceChaos)
        {
            reason = $"{newPrice}x {targetOrb} is not cheaper than {oldPrice}x {orbType}";
            return 0;
        }

        var cap = LowerPriceStepDownMaxAmount;
        if (newPrice > cap)
        {
            reason = $"the converted amount ({newPrice}x {targetOrb}) is above the {cap} cap";
            return 0;
        }

        refusal = LowerPriceStepDownRefusal.None;
        return newPrice;
    }

    /// <summary>
    /// Whether the poe.ninja table is recent enough to price a real listing against. The value
    /// display can live with stale numbers; a conversion that relists an item cannot.
    /// </summary>
    private bool IsLowerPriceRateTableFresh(out string reason)
    {
        reason = null;

        if (_lowerPriceLastCurrencyUpdate == DateTime.MinValue)
        {
            reason = "poe.ninja rates have not loaded yet";
            return false;
        }

        // The table has to belong to the league the items are actually listed in. Standard prices a
        // Divine near 829 chaos against roughly 174 in the current challenge league, so pricing off
        // the wrong one relists items at about five times the intended number.
        var league = ResolveLeagueOrNull();
        if (league == null)
        {
            reason = $"the league isn't confirmed ({LeagueUnresolvedReason()})";
            return false;
        }

        if (!string.Equals(league, _lowerPriceRatesLeague, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"the loaded rates are for '{_lowerPriceRatesLeague ?? "?"}' but the league is '{league}'";
            return false;
        }

        // Two intervals of slack: one missed refresh is normal, a run of them means the fetch is
        // failing and the numbers are drifting away from the market.
        var maxAge = TimeSpan.FromMinutes(LowerPriceRateRefreshMinutes * 2);
        var age = DateTime.Now - _lowerPriceLastCurrencyUpdate;
        if (age > maxAge)
        {
            reason = $"poe.ninja rates are {age.TotalMinutes:F0} minutes stale";
            return false;
        }

        return true;
    }

    // ===== Stepping a listing onto a cheaper currency =====
    //
    // The in-currency reprice gets away with "right-click, type, Enter" because it only touches the
    // amount. Changing the currency means driving the Set Item Price dialog, and that dialog is
    // rough to automate: the offline merchant uses the generic PopUpWindow rather than ExileCore's
    // typed ItemRightClickPriceMenu, and the currency list is drawn by the client with no backing
    // elements at all. A scan of every element under UIRoot with the list open found no row for any
    // currency name, and the dropdown's own label is just as unreadable.
    //
    // So a row is picked by clicking its slot on a grid derived from the list's scrollbar, which IS
    // a real element, and the only proof the right currency landed is the item's tooltip afterwards.

    // Children of PopUpWindow[2][0] - the control row along the bottom of the dialog.
    private const int LowerPriceAmountControlIndex = 0;
    private const int LowerPriceCurrencyControlIndex = 1;
    private const int LowerPriceListButtonControlIndex = 2;

    // Child of the currency dropdown: the option list's scrollbar, visible only while it is open.
    private const int LowerPriceCurrencyScrollbarIndex = 2;

    // How many rows the list shows at once. This is a UI constant, unlike the row height in pixels,
    // so deriving the height from the scrollbar track keeps the grid right at any resolution.
    private const int LowerPriceCurrencyVisibleRows = 14;

    /// <summary>
    /// Where each ladder currency sits in the dropdown. Clicking a slot is the only way to choose
    /// one, so this order is load-bearing - it was read off the live client on 2026-07-31. Every
    /// conversion re-reads the item tooltip afterwards and stops the run if the order has moved,
    /// which is what keeps a reordered list from quietly mispricing a whole tab.
    /// </summary>
    private static readonly Dictionary<string, int> LowerPriceCurrencyRowIndex =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [ChaosOrbName] = 0,
            [DivineOrbName] = 1,
            [MirrorOrbName] = 5,
        };

    /// <summary>
    /// Relists the item under the cursor as <paramref name="amount"/>x <paramref name="targetOrb"/>.
    /// Returns false with the listing untouched if any step doesn't land, and puts the cursor back
    /// on the item at <paramref name="itemPosition"/> so the caller's fallback path still aims at it.
    /// </summary>
    private async Task<bool> TryLowerPriceStepDownListing(int amount, string targetOrb, Vector2 itemPosition,
                                                          Action onItemNotPriceable = null)
    {
        if (!LowerPriceCurrencyRowIndex.TryGetValue(targetOrb, out var rowIndex))
        {
            LogError($"LowerPrice: no known dropdown row for '{targetOrb}', so it can't be selected.");
            return false;
        }

        // Same gesture the in-currency path uses to start an edit.
        Utility.Mouse.RightDown();
        await LowerPriceInputDelay();
        Utility.Mouse.RightUp();

        var dialog = await WaitForLowerPriceDialog(LowerPriceDialogOpenTimeoutMs);
        if (dialog == null)
        {
            // Distinguish "nothing opened" from "something else opened", because the second one
            // means the title check saved us from driving an unrelated popup.
            var popUp = GameController?.IngameState?.IngameUi?.PopUpWindow;
            var title = ReadLowerPriceDialogTitle(popUp);

            if (popUp?.IsVisible == true && !string.IsNullOrWhiteSpace(title))
            {
                LogError($"LowerPrice: right-click opened '{title}', not the {LowerPriceDialogTitle} dialog; leaving it alone.");
            }
            else
            {
                // The item can't be priced at all - locked. Tell the caller so it skips this item
                // outright instead of falling through and paying the same timeout a second time.
                onItemNotPriceable?.Invoke();
            }

            return false;
        }

        var committed = false;
        try
        {
            committed = await TrySelectLowerPriceCurrencyRow(dialog, rowIndex, targetOrb)
                     && await TrySetLowerPriceAmount(dialog, amount)
                     && await TryCommitLowerPriceDialog(dialog);
        }
        catch (Exception ex)
        {
            LogError($"LowerPrice: step down to {targetOrb} threw mid-edit ({ex.GetType().Name}: {ex.Message}).");
        }

        if (!committed)
        {
            await CancelLowerPriceDialog();

            // Picking a currency leaves the cursor down on the dropdown, and the caller's fallback
            // right-clicks wherever the cursor happens to be. Put it back on the item first.
            Utility.Mouse.moveMouse(itemPosition);
            await LowerPriceActionStep();
        }

        return committed;
    }

    private const string LowerPriceDialogTitle = "Set Item Price";

    /// <summary>
    /// The Set Item Price dialog, or null when it isn't open. PopUpWindow is a shared slot — the
    /// same address also backs DestroyConfirmationWindow and others — so the title is checked
    /// before anything gets clicked at fixed child indices. Clicking blind into the wrong popup is
    /// exactly the kind of mistake that isn't recoverable.
    /// </summary>
    private Element GetLowerPriceDialog()
    {
        var popUp = GameController?.IngameState?.IngameUi?.PopUpWindow;
        if (popUp?.IsVisible != true) return null;

        return string.Equals(ReadLowerPriceDialogTitle(popUp), LowerPriceDialogTitle, StringComparison.OrdinalIgnoreCase)
            ? popUp
            : null;
    }

    private static string ReadLowerPriceDialogTitle(Element popUp)
    {
        try
        {
            var title = popUp?.Children?.ElementAtOrDefault(0)?.Children?.ElementAtOrDefault(0);
            return (title?.TextNoTags ?? title?.Text)?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private async Task<Element> WaitForLowerPriceDialog(int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var dialog = GetLowerPriceDialog();
            if (dialog != null) return dialog;
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) return null;
            await Task.Delay(LowerPriceUiPollMs);
        }
    }

    /// <summary>One of the three controls along the bottom of the dialog: amount, currency, commit.</summary>
    private static Element GetLowerPriceDialogControl(Element dialog, int controlIndex)
    {
        try
        {
            var controlRow = dialog?.Children?.ElementAtOrDefault(2)?.Children?.ElementAtOrDefault(0);
            return controlRow?.Children?.ElementAtOrDefault(controlIndex);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLowerPriceCurrencyListOpen(Element dropdown)
    {
        try
        {
            var scrollbar = dropdown?.Children?.ElementAtOrDefault(LowerPriceCurrencyScrollbarIndex);
            return scrollbar?.IsVisibleLocal == true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TrySelectLowerPriceCurrencyRow(Element dialog, int rowIndex, string targetOrb)
    {
        var dropdown = GetLowerPriceDialogControl(dialog, LowerPriceCurrencyControlIndex);
        if (dropdown == null)
        {
            LogError("LowerPrice: the price dialog has no currency dropdown where one was expected.");
            return false;
        }

        if (!IsLowerPriceCurrencyListOpen(dropdown))
        {
            await LowerPriceClickElement(dropdown.GetClientRectCache);
            if (!await WaitForLowerPriceCondition(() => IsLowerPriceCurrencyListOpen(dropdown), 1500))
            {
                LogError("LowerPrice: the currency list didn't open.");
                return false;
            }
        }

        if (!TryGetLowerPriceCurrencyRowRect(dropdown, rowIndex, out var rowRect))
        {
            LogError($"LowerPrice: can't place row {rowIndex} ('{targetOrb}') on the currency list grid.");
            return false;
        }

        await LowerPriceClickElement(rowRect);

        // The list closing is the only signal available here. Which row it landed on is genuinely
        // unreadable, so the tooltip check after the commit is what actually proves the currency.
        if (!await WaitForLowerPriceCondition(() => !IsLowerPriceCurrencyListOpen(dropdown), 1500))
        {
            LogError("LowerPrice: the currency list stayed open after clicking a row.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Screen rect of a row in the open currency list. The rows themselves aren't in the element
    /// tree, but the list's scrollbar is, and the rows sit on an even grid down that track.
    /// </summary>
    private static bool TryGetLowerPriceCurrencyRowRect(Element dropdown, int rowIndex, out RectangleF rect)
    {
        rect = default;

        var scrollbar = dropdown?.Children?.ElementAtOrDefault(LowerPriceCurrencyScrollbarIndex);
        if (scrollbar == null || !scrollbar.IsVisibleLocal) return false;

        var track = scrollbar.GetClientRectCache;
        if (track.Height <= 0) return false;

        // The grid only means anything from the top of the list. If something scrolled it, the row
        // under a given slot is no longer the row this method claims it is.
        var thumb = scrollbar.Children?.ElementAtOrDefault(2);
        if (thumb != null && thumb.GetClientRectCache.Y > track.Y + 2f) return false;

        var rowHeight = track.Height / LowerPriceCurrencyVisibleRows;
        var top = track.Y + rowIndex * rowHeight;
        if (top + rowHeight > track.Y + track.Height) return false; // would need scrolling

        // Span the row between the dropdown's left edge and the scrollbar column, inset at both
        // ends. Proportional rather than a fixed pixel inset, so it holds at any resolution.
        var listLeft = dropdown.GetClientRectCache.X;
        var listWidth = track.X - listLeft;
        if (listWidth <= 20f) return false;

        rect = new RectangleF(listLeft + listWidth * 0.15f, top, listWidth * 0.7f, rowHeight);
        return true;
    }

    private async Task<bool> TrySetLowerPriceAmount(Element dialog, int amount)
    {
        var input = GetLowerPriceDialogControl(dialog, LowerPriceAmountControlIndex);
        if (input == null)
        {
            LogError("LowerPrice: the price dialog has no amount field where one was expected.");
            return false;
        }

        await LowerPriceClickElement(input.GetClientRectCache);

        // Clicking in drops the caret into the existing number rather than replacing it, so select
        // what's already there first - otherwise the new digits splice into the old price.
        Utility.Keyboard.KeyDown(Keys.LControlKey);
        Utility.Keyboard.KeyPress(Keys.A);
        Utility.Keyboard.KeyUp(Keys.LControlKey);
        await LowerPriceInputDelay();

        Utility.Keyboard.Type(amount.ToString(CultureInfo.InvariantCulture));
        await LowerPriceInputDelay();

        // This field, unlike the dropdown, does expose its text - so a mistyped amount is catchable
        // before anything gets committed.
        var typed = ReadLowerPriceAmountField(input);
        if (typed.HasValue && typed.Value != amount)
        {
            LogError($"LowerPrice: the amount field reads {typed.Value} after typing {amount}; not committing.");
            return false;
        }

        return true;
    }

    private static int? ReadLowerPriceAmountField(Element input)
    {
        string text = null;
        try { text = input?.TextNoTags ?? input?.Text; } catch { }
        if (string.IsNullOrWhiteSpace(text)) return null;

        var digits = new string(text.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, out var value) ? value : (int?)null;
    }

    private async Task<bool> TryCommitLowerPriceDialog(Element dialog)
    {
        var listButton = GetLowerPriceDialogControl(dialog, LowerPriceListButtonControlIndex);
        if (listButton == null)
        {
            LogError("LowerPrice: the price dialog has no List Item button where one was expected.");
            return false;
        }

        await LowerPriceClickElement(listButton.GetClientRectCache);

        if (!await WaitForLowerPriceCondition(() => GetLowerPriceDialog() == null, 2000))
        {
            LogError("LowerPrice: the price dialog stayed open after clicking List Item.");
            return false;
        }

        return true;
    }

    private async Task CancelLowerPriceDialog()
    {
        if (GetLowerPriceDialog() == null) return;

        Utility.Keyboard.KeyPress(Keys.Escape);
        await LowerPriceActionStep();

        if (GetLowerPriceDialog() != null)
            LogError("LowerPrice: couldn't close the price dialog; stop the run and check the listing by hand.");
    }

    private enum LowerPriceStepDownVerdict
    {
        /// <summary>The tooltip reads back exactly what was set.</summary>
        Confirmed,

        /// <summary>Couldn't confirm — no readable tooltip, or it still shows the old listing.</summary>
        Unverified,

        /// <summary>The tooltip shows a listing nobody asked for. The dropdown row order has moved.</summary>
        WrongCurrency,
    }

    /// <summary>
    /// Checks what a step down actually produced, by re-hovering the item and reading its tooltip.
    /// This is the entire safety net for the unreadable dropdown.
    ///
    /// The three outcomes are deliberately kept apart. Only <see cref="LowerPriceStepDownVerdict.WrongCurrency"/>
    /// means something went wrong — a tooltip that won't rebuild in time, or one still showing the
    /// pre-relist price, says nothing about whether the listing is correct. Treating those as
    /// failures is what used to abandon the rest of the tab after the first successful step down.
    /// </summary>
    private async Task<LowerPriceStepDownVerdict> VerifyLowerPriceStepDown(
        NormalInventoryItem item, Vector2 itemPosition,
        int expectedAmount, string expectedOrb,
        int previousAmount, string previousOrb)
    {
        // Committing the dialog can leave the cursor on the item already, and moving to the pixel
        // it's already on is not a move — the client never re-hovers and the tooltip never rebuilds.
        // Step off first so there's a real hover transition.
        Utility.Mouse.moveMouse(new Vector2(itemPosition.X, itemPosition.Y - 80));
        await LowerPriceInputDelay();
        Utility.Mouse.moveMouse(itemPosition);
        await LowerPriceInputDelay();

        var sawOldListing = false;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Bounded tightly: this runs once per run, and a tooltip that hasn't rebuilt within a
        // second isn't going to.
        while (stopwatch.ElapsedMilliseconds < 1200)
        {
            if (TryReadLowerPriceCurrentPrice(item, out var price, out var orbType))
            {
                var orb = orbType?.Trim();

                if (price == expectedAmount && string.Equals(orb, expectedOrb, StringComparison.OrdinalIgnoreCase))
                    return LowerPriceStepDownVerdict.Confirmed;

                if (price == previousAmount && string.Equals(orb, previousOrb, StringComparison.OrdinalIgnoreCase))
                {
                    // Stale tooltip, not a bad price. Keep waiting for the client to catch up.
                    sawOldListing = true;
                }
                else
                {
                    LogError($"LowerPrice: step down produced {price}x {orb}, not {expectedAmount}x {expectedOrb}. " +
                             "The client's currency row order no longer matches LowerPriceCurrencyRowIndex - fix " +
                             "that before running again.");
                    return LowerPriceStepDownVerdict.WrongCurrency;
                }
            }

            await Task.Delay(50);
        }

        LogMessage(sawOldListing
            ? $"LowerPrice: relisted as {expectedAmount}x {expectedOrb}, but the tooltip still showed the old " +
              $"{previousAmount}x {previousOrb} - couldn't confirm it, carrying on."
            : $"LowerPrice: relisted as {expectedAmount}x {expectedOrb}, but couldn't re-read the item's price " +
              "to confirm it - carrying on.");

        return LowerPriceStepDownVerdict.Unverified;
    }

    /// <summary>
    /// Reads the item's asking price after a relist. Tries the captured element first, then whatever
    /// the client currently reports as hovered — relisting rebuilds the merchant panel's item list,
    /// which leaves the captured reference pointing at an address that no longer holds a tooltip.
    /// </summary>
    private bool TryReadLowerPriceCurrentPrice(NormalInventoryItem item, out int price, out string orbType)
    {
        if (TryReadLowerPriceTooltipPrice(item?.Tooltip, out price, out orbType)) return true;

        Element hoverTooltip = null;
        try { hoverTooltip = GameController?.IngameState?.UIHoverTooltip; } catch { }
        if (TryReadLowerPriceTooltipPrice(hoverTooltip, out price, out orbType)) return true;

        Element hovered = null;
        try { hovered = GameController?.IngameState?.UIHover?.Tooltip; } catch { }
        return TryReadLowerPriceTooltipPrice(hovered, out price, out orbType);
    }

    /// <summary>
    /// Reads "Asking Price: Nx &lt;Currency&gt;" off a merchant item's hover tooltip, the same shape
    /// the reprice loop parses inline.
    /// </summary>
    private static bool TryReadLowerPriceTooltipPrice(Element tooltip, out int price, out string orbType)
    {
        price = 0;
        orbType = null;

        try
        {
            var priceRow = tooltip?.Children?.ElementAtOrDefault(0)
                                  ?.Children?.ElementAtOrDefault(1)
                                  ?.Children?.LastOrDefault();

            var priceGroup = priceRow?.Children?.ElementAtOrDefault(1);
            if (priceGroup?.Children == null || priceGroup.Children.Count < 3) return false;

            var priceText = priceGroup.Children[0]?.Text;
            if (priceText == null || !priceText.EndsWith("x")) return false;
            if (!int.TryParse(priceText.Replace("x", "").Replace(",", "").Trim(), out price)) return false;

            orbType = priceGroup.Children[2]?.Text;
            if (!IsPlausibleLowerPriceOrbName(orbType))
            {
                orbType = null;
                return false;
            }

            orbType = orbType.Trim();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="orbType"/> reads like a currency name rather than a garbled memory
    /// read.
    ///
    /// The merchant panel rebuilds its item list constantly, and a tooltip read across a rebuild
    /// hands back arbitrary bytes rather than failing. Those arrived as orb names made of
    /// unrenderable glyphs, showed up on the value display as "???: 68", and were then quietly
    /// dropped from the totals as "an unpriced currency" - so a wrong number looked like a correct
    /// one. Every real orb name is a plain ASCII word or two, so anything else is a failed read to
    /// retry, not an exotic currency to count.
    /// </summary>
    private static bool IsPlausibleLowerPriceOrbName(string orbType)
    {
        if (string.IsNullOrWhiteSpace(orbType)) return false;

        var name = orbType.Trim();
        if (name.Length < 3 || name.Length > 40) return false;

        var letters = 0;
        foreach (var c in name)
        {
            if (c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z') { letters++; continue; }
            // Apostrophes and hyphens show up in names like "Gemcutter's Prism"; nothing else does.
            if (c == ' ' || c == '\'' || c == '-') continue;
            return false;
        }

        return letters >= 3;
    }

    /// <summary>Left-clicks the centre of a UI rect, which the client reports window-relative.</summary>
    private async Task LowerPriceClickElement(RectangleF rect)
    {
        var windowTopLeft = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
        var target = new Vector2(windowTopLeft.X + rect.X + rect.Width / 2f,
                                 windowTopLeft.Y + rect.Y + rect.Height / 2f);

        // A click is one gesture, not three actions. This used to spend a full action delay after
        // the move, between the press and release, and again afterwards - roughly a quarter second
        // per click, times four clicks per step down. The press/release only needs to outlast a
        // frame; the settle afterwards is what the client actually needs.
        Utility.Mouse.moveMouse(target);
        await LowerPriceInputDelay();
        Utility.Mouse.LeftDown();
        await Task.Delay(LowerPriceClickHoldMs);
        Utility.Mouse.LeftUp();
        await LowerPriceInputDelay();
    }

    /// <summary>Full settle between distinct actions.</summary>
    private async Task LowerPriceActionStep()
    {
        await TaskUtils.NextFrame();
        await Task.Delay(LowerPriceStepDelayMs);
    }

    /// <summary>
    /// Short settle within a single gesture. Deliberately does NOT wait on a render frame: this only
    /// spaces out synthesised input, and the plugin doesn't read any UI state across it. The waits
    /// that DO need fresh state poll for it explicitly. Ten of these per step down were each costing
    /// a full frame tick on top of the sleep, for nothing.
    /// </summary>
    private static Task LowerPriceInputDelay() => Task.Delay(LowerPriceClickHoldMs);

    /// <summary>True while the chat input has the keyboard, i.e. anything typed goes to a channel.</summary>
    private bool IsLowerPriceChatOpen()
    {
        try
        {
            // ChatBox.IsVisible is the chat *log* and reads true permanently; the input element is
            // the one that only appears once Enter has focused it.
            return GameController?.IngameState?.IngameUi?.ChatPanel?.ChatInputElement?.IsVisible == true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForLowerPriceCondition(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (condition()) return true;
            }
            catch
            {
                // Element torn down between frames; treat as "not yet" and keep polling.
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(LowerPriceUiPollMs);
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

    /// <summary>
    /// The client draws a padlock on items it won't let you reprice, and that padlock has no
    /// element behind it - no texture, no distinct tooltip type, nothing overlapping the cell. What
    /// it DOES have is a line in the item's own tooltip:
    ///
    ///   "You assigned a price to this item recently, and cannot modify or remove the item yet."
    ///
    /// which is free to read, because the reprice loop is already holding that tooltip to parse the
    /// asking price out of it. The previous implementation walked item.Children looking for a
    /// LockedItems.dds texture; merchant item elements have no children at all, so it always
    /// returned false and locked items were repriced anyway.
    /// </summary>
    private const string LowerPriceLockedNotice = "cannot modify or remove";

    private static bool IsLowerPriceItemLocked(NormalInventoryItem item)
    {
        Element tooltip = null;
        try { tooltip = item?.Tooltip; } catch { }
        if (tooltip == null) return false;

        // Depth-first over the tooltip. The notice sits among the item's other lines, and its
        // position shifts the asking-price row down, so don't look for it at a fixed index.
        var stack = new Stack<Element>();
        stack.Push(tooltip);
        var visited = 0;

        while (stack.Count > 0 && visited++ < 400)
        {
            var element = stack.Pop();
            if (element == null) continue;

            string text = null;
            try { text = element.TextNoTags ?? element.Text; } catch { }

            if (!string.IsNullOrEmpty(text) &&
                text.IndexOf(LowerPriceLockedNotice, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            IList<Element> children = null;
            try { children = element.Children; } catch { }
            if (children == null) continue;
            foreach (var child in children) stack.Push(child);
        }

        return false;
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
            var timerDuration = TimeSpan.FromMinutes(LowerPriceTimerMinutes);
            var timeRemaining = timerDuration - timeSinceLastReprice;

            if (timeRemaining <= TimeSpan.Zero)
            {
                // Timer expired
                if (!_lowerPriceTimerExpired)
                {
                    _lowerPriceTimerExpired = true;
                    PlayLowerPriceSoundNotification();
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
        // Rates always refresh. Leaving them stale is never what anyone wanted, and the step-down
        // refuses to price against an old table anyway.

        // Resolve the league before the interval check, not after. Prices are only meaningful for
        // one league, so a league that has since resolved has to invalidate the table immediately
        // rather than wait out the refresh interval.
        //
        // Deliberately the LENIENT resolver. Gating the whole table on an authoritative answer left
        // the value display dead whenever the character's league couldn't be confirmed — no
        // POESESSID, at the login screen, mid-loading-screen. The display is read-only, so a
        // best-guess league is fine there as long as it says which league it used. Repricing still
        // demands the authoritative answer and refuses without it; see IsLowerPriceRateTableFresh.
        string league = ResolveLeague();
        if (string.IsNullOrWhiteSpace(league)) return;

        var leagueChanged = !string.Equals(league, _lowerPriceRatesLeague, StringComparison.OrdinalIgnoreCase);

        var timeSinceUpdate = DateTime.Now - _lowerPriceLastCurrencyUpdate;
        if (!leagueChanged && timeSinceUpdate.TotalMinutes < LowerPriceRateRefreshMinutes) return;
        if (DateTime.Now < _lowerPriceRatesNextAttempt) return;

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
                {
                    _lowerPriceRatesNextAttempt = DateTime.Now.AddMinutes(2);
                    return; // Keep default rates; non-fatal.
                }
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
                _lowerPriceRatesNextAttempt = DateTime.Now.AddMinutes(2);
                return;
            }

            lock (_lowerPriceCurrencyRatesLock)
            {
                _lowerPriceChaosValues = parsed;
                _lowerPriceChaosValues["Chaos Orb"] = 1m;
            }

            _lowerPriceRatesLeague = league;
            _lowerPriceLastCurrencyUpdate = DateTime.Now;
            _lowerPriceRatesNextAttempt = DateTime.MinValue;
            LogMessage($"LowerPrice: loaded {parsed.Count} currency rates for league '{league}' " +
                       $"(1 Divine = {GetLowerPriceChaosValue(DivineOrbName):F1} chaos).");
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

            var pos = new Vector2(LowerPriceValueDisplayX, LowerPriceValueDisplayY);
            var totalItemsInTab = items?.Count() ?? 0;

            // Prefer the last all-tabs scan for this tab. Reading prices out of the game means
            // reading hover tooltips, and the client doesn't build a tooltip until you actually
            // hover the item — so the in-game path can only ever see what you've already touched.
            // The API scan has every price whether or not you hovered anything.
            var scanned = GetScannedValueForOpenTab();
            var divineInChaos = GetLowerPriceChaosValue(DivineOrbName);

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
                displayText += LowerPriceRatesLeagueLabel();

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
                displayText += LowerPriceRatesLeagueLabel();

                if (itemValues.UnpricedItems > 0)
                    displayText += $"\n({itemValues.UnpricedItems} item(s) in an unpriced currency)";

                // Only relevant on the tooltip path; a scan makes hovering unnecessary.
                if (items != null && items.Count(i => i.Tooltip != null) < totalItemsInTab)
                {
                    displayText += $"\n⚠️ Not scanned — hover items, or press " +
                                   $"{LowerPriceSettings.StashScanHotkey.Value} to scan all tabs.";
                }
            }

            // Items the last run deliberately didn't touch. Loudest line on the display, because a
            // run that skipped half the tab otherwise looks identical to one that worked.
            var blocked = _lowerPriceRunStepDownBlocked;
            if (blocked > 0)
            {
                displayText += $"\n\n⚠️ {blocked} item(s) NOT repriced — step down was due but" +
                               $"\n   couldn't be made: {_lowerPriceRunStepDownReason}";
            }

            // Totals from the last full-shop scan, under the current tab's figures.
            displayText += LowerPriceShopScanSummary();

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
                    // Locked items are deliberately NOT skipped here. They're still listed and still
                    // worth what they're priced at, so they belong in the tab's value. The old lock
                    // check never actually matched, so they were always counted - now that it works,
                    // skipping them here would silently drop them out of the totals.

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
        var divineInChaos = GetLowerPriceChaosValue(DivineOrbName);
        summary.DivineRateKnown = divineInChaos > 0;
        if (summary.DivineRateKnown)
            summary.TotalInDivine = summary.TotalInChaos / divineInChaos;

        return summary;
    }

    /// <summary>
    /// Says which league the loaded rates belong to, and flags it when that isn't the league being
    /// played. Without this the totals look authoritative no matter which league they came from —
    /// which is exactly how a Divine got valued at 829c instead of 174c.
    /// </summary>
    private string LowerPriceRatesLeagueLabel()
    {
        var rateLeague = _lowerPriceRatesLeague;
        if (string.IsNullOrWhiteSpace(rateLeague))
            return "\n(no currency rates loaded yet)";

        var actual = ResolveLeagueOrNull();
        if (actual == null)
        {
            var label = $"\n⚠️ rates: {rateLeague} — league UNCONFIRMED, step down disabled" +
                        $"\n   {LeagueUnresolvedReason()}";

            // Spelling has to match exactly, so show what's accepted rather than making it a guess.
            var known = KnownLeagueNames();
            if (known != null) label += $"\n   valid names: {known}";

            return label;
        }

        if (!string.Equals(actual, rateLeague, StringComparison.OrdinalIgnoreCase))
            return $"\n⚠️ rates are {rateLeague} but you're in {actual} — step down disabled";

        // Rates match the league, so the only thing left that can block a step down is their age.
        return IsLowerPriceRateTableFresh(out var staleReason)
            ? $"\n(rates: {rateLeague})"
            : $"\n⚠️ step down disabled — {staleReason}";
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

