using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.Shared.Enums;
using RectangleF = SharpDX.RectangleF;

namespace TradeUtils;

// ===== Collecting filled Currency Exchange orders =====
//
// A filled order has to be pulled out by hand: ctrl+right-click the order's Buying slot to move as
// much as fits into the inventory, then ctrl+right-click the inventory stacks to send them to the
// open stash tab, and repeat until the order is empty. For a 50,000 chaos order that is a lot of
// clicking. This does the same loop.
//
// ExileCore's typed CurrencyExchangePanel is UNUSABLE on this patch: with the panel plainly open on
// screen it reports IsVisible=false, Orders.Count=0, and a rect covering nearly the whole screen.
// So none of the nice typed fields (IsCompleted, OfferedItemStackSize, ...) can be read, and this
// drives the raw element tree instead - exactly like the merchant's Set Item Price dialog.
public partial class TradeUtils
{
    // The panel is not where ExileCore points, so it gets found by shape instead. Verified live:
    // exactly one of the 192 children of UIRoot->[1] matches, so this is not ambiguous.
    private const int ExchangeUiLayerIndex = 1;
    private const int ExchangePlaceOrderIndex = 16;
    private const string ExchangePlaceOrderText = "place order";
    private const int ExchangeMinPanelChildren = 21;

    // Panel -> [20] orders region -> [2] the visible order list -> one child per placed order.
    private const int ExchangeOrdersRegionIndex = 20;
    private const int ExchangeOrderListIndex = 2;

    // Inside an order row: [4] is the Buying slot (what you receive), [5] the Selling slot. Each
    // slot has [0] the item icon and [1] the stack count. The icon's visibility is the reliable
    // "is there anything in here" signal - an empty slot keeps its count text at "0" but hides the
    // icon, which beats parsing an abbreviated "50.7K".
    private const int ExchangeOrderBuySlotIndex = 4;
    private const int ExchangeSlotIconIndex = 0;
    private const int ExchangeSlotCountIndex = 1;

    // The panel's own stash button, the chest icon hanging off the panel's left edge at (669,1062).
    // Collecting opens the inventory by itself but never the stash.
    private const int ExchangeStashButtonIndex = 18;

    // The order list is a fixed-width column inside the much wider orders region (486px, measured
    // live); the button goes in the empty parchment to its right. Deliberately NOT beside Place
    // Order: that button's frame reaches to x+234, which puts a 37px button within a few pixels of
    // it, and a stray click there places a real currency order.
    private const float ExchangeOrderListWidth = 486f;
    private const float ExchangeCollectButtonGap = 12f;

    private const int ExchangeCollectMaxCycles = 60;
    private const int ExchangeCollectClickHoldMs = 15;
    private const int ExchangeCollectPollMs = 8;
    // Moving a 50,000-strong stack can take the client a moment, and a timeout here reads as "the
    // stash is full" and stops the run - so it is generous, and every wait gets a second attempt.
    private const int ExchangeCollectMoveTimeoutMs = 2500;
    private const int ExchangeCollectAttempts = 2;
    private const int ExchangeCollectButtonSize = 37;

    private readonly ConcurrentDictionary<RectangleF, bool?> _exchangeCollectMouseState = new();
    private int _exchangeCollectRunning; // 0 = idle, 1 = a collect run is in flight
    private volatile string _exchangeCollectStatus;

    // Re-resolving the panel means walking 192 children, which is too much to do every frame.
    private Element _exchangeePanelCache;
    private DateTime _exchangePanelCacheAt = DateTime.MinValue;

    /// <summary>
    /// The live Currency Exchange panel, found by shape rather than by ExileCore's typed accessor
    /// (which is stale on this patch). Cached briefly because the scan is 192 children wide.
    /// </summary>
    private Element FindCurrencyExchangePanel()
    {
        var cached = _exchangeePanelCache;
        if (cached != null && (DateTime.Now - _exchangePanelCacheAt).TotalMilliseconds < 500)
        {
            try
            {
                if (cached.IsVisibleLocal) return cached;
            }
            catch
            {
                // Torn down between frames; fall through and re-resolve.
            }
        }

        _exchangeePanelCache = null;

        try
        {
            var layer = GameController?.IngameState?.UIRoot?.Children?[ExchangeUiLayerIndex];
            if (layer == null) return null;

            var children = layer.Children;
            if (children == null) return null;

            foreach (var candidate in children)
            {
                if (candidate == null) continue;

                bool visible;
                try { visible = candidate.IsVisibleLocal; } catch { continue; }
                if (!visible) continue;

                try { if (candidate.ChildCount < ExchangeMinPanelChildren) continue; } catch { continue; }

                var button = NavigateLowerPriceChildren(candidate, ExchangePlaceOrderIndex, 0);
                string text = null;
                try { text = button?.Text; } catch { }

                if (string.IsNullOrEmpty(text) ||
                    text.IndexOf(ExchangePlaceOrderText, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                _exchangeePanelCache = candidate;
                _exchangePanelCacheAt = DateTime.Now;
                return candidate;
            }
        }
        catch (Exception ex)
        {
            LogError($"ExchangeCollect: couldn't look for the exchange panel ({ex.Message}).");
        }

        return null;
    }

    /// <summary>
    /// Finds the panel, tolerating the odd frame where it reads as missing while the UI rebuilds.
    /// A single failed lookup used to end the run as "exchange panel closed" with the panel still
    /// plainly open, which is the most likely reason a collect stopped part-way through.
    /// </summary>
    private async Task<Element> FindExchangePanelWithRetry(int timeoutMs = 800)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var panel = FindCurrencyExchangePanel();
            if (panel != null) return panel;
            if (stopwatch.ElapsedMilliseconds >= timeoutMs) return null;
            await Task.Delay(ExchangeCollectPollMs);
        }
    }

    /// <summary>
    /// Makes sure the stash is open, via the exchange panel's own stash button. Ctrl+right-clicking
    /// a filled order opens the inventory by itself but never the stash, and with no stash open
    /// every ctrl+right-click in the inventory does nothing - which the drain loop can only read as
    /// "this stack won't move", i.e. a full tab.
    /// </summary>
    private async Task<bool> EnsureExchangeStashOpen(Element panel)
    {
        if (IsExchangeStashOpen()) return true;

        var button = NavigateLowerPriceChildren(panel, ExchangeStashButtonIndex);
        if (button == null) return false;

        var rect = button.GetClientRectCache;
        if (rect.Width <= 0 || rect.Height <= 0) return false;

        await LowerPriceClickElement(rect);

        // Verified, not assumed. If this is ever the wrong child index the wait simply fails and the
        // run stops with a message, rather than having clicked something unknown and carried on.
        return await WaitForExchangeCondition(IsExchangeStashOpen, 2500);
    }

    private bool IsExchangeStashOpen()
    {
        try { return GameController?.IngameState?.IngameUi?.StashElement?.IsVisible == true; }
        catch { return false; }
    }

    /// <summary>Order rows with something sitting in their Buying slot, ready to be pulled out.</summary>
    private List<Element> CollectableExchangeOrders(Element panel)
    {
        var ready = new List<Element>();
        var list = NavigateLowerPriceChildren(panel, ExchangeOrdersRegionIndex, ExchangeOrderListIndex);
        if (list == null) return ready;

        IList<Element> rows;
        try { rows = list.Children; } catch { return ready; }
        if (rows == null) return ready;

        foreach (var row in rows)
        {
            if (row == null) continue;

            var icon = NavigateLowerPriceChildren(row, ExchangeOrderBuySlotIndex, ExchangeSlotIconIndex);
            if (icon == null) continue;

            try { if (!icon.IsVisibleLocal) continue; } catch { continue; }

            ready.Add(row);
        }

        return ready;
    }

    /// <summary>The stack label on an order's Buying slot, e.g. "50.7K". Display only.</summary>
    private string ExchangeOrderBuyingLabel(Element row)
    {
        var count = NavigateLowerPriceChildren(row, ExchangeOrderBuySlotIndex, ExchangeSlotCountIndex);
        try { return count?.Text; } catch { return null; }
    }

    /// <summary>Items currently sitting in the player's inventory, re-read rather than cached.</summary>
    private IList<NormalInventoryItem> CurrentExchangeInventoryItems()
    {
        try
        {
            return GameController?.IngameState?.IngameUi?.InventoryPanel?[InventoryIndex.PlayerInventory]
                                 ?.VisibleInventoryItems;
        }
        catch
        {
            return null;
        }
    }

    private int CurrentExchangeInventoryCount() => CurrentExchangeInventoryItems()?.Count ?? 0;

    /// <summary>
    /// The inventory stack to send next, chosen from the END of the list.
    ///
    /// The inventory fills top-left first and the Currency Exchange panel sits over the left edge of
    /// the grid, so the earliest items are exactly the ones underneath it - ctrl+right-clicking
    /// there hits the panel rather than the stack, and nothing moves. Walking backwards takes the
    /// stacks furthest from the panel first. Returns null when everything left is covered, which the
    /// caller reports instead of clicking blind into the panel.
    /// </summary>
    private static NormalInventoryItem PickExchangeInventoryStack(IList<NormalInventoryItem> items,
                                                                  RectangleF panelRect)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item == null) continue;

            RectangleF rect;
            try { rect = item.GetClientRectCache; } catch { continue; }
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            // Any overlap disqualifies it - a click lands on whichever element is drawn on top.
            if (rect.X < panelRect.X + panelRect.Width && rect.X + rect.Width > panelRect.X &&
                rect.Y < panelRect.Y + panelRect.Height && rect.Y + rect.Height > panelRect.Y)
                continue;

            return item;
        }

        return null;
    }

    /// <summary>
    /// Right mouse held down deliberately, as opposed to one of our own synthetic clicks. The action
    /// this feature performs IS a right-click, so a single sample would let the run cancel itself;
    /// requiring the button to still be down 60ms later can't be confused with a 15ms synthetic one.
    /// </summary>
    private static async Task<bool> ExchangeCollectCancelRequested()
    {
        if ((Control.MouseButtons & MouseButtons.Right) == 0) return false;
        await Task.Delay(60);
        return (Control.MouseButtons & MouseButtons.Right) != 0;
    }

    /// <summary>Ctrl+right-click at the centre of <paramref name="rect"/>.</summary>
    private async Task ExchangeCtrlRightClick(RectangleF rect)
    {
        var windowTopLeft = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
        var target = new Vector2(windowTopLeft.X + rect.X + rect.Width / 2f,
                                 windowTopLeft.Y + rect.Y + rect.Height / 2f);

        Utility.Mouse.moveMouse(target);
        await Task.Delay(ExchangeCollectClickHoldMs);

        Utility.Keyboard.KeyDown(Keys.LControlKey);
        await Task.Delay(ExchangeCollectClickHoldMs);
        Utility.Mouse.RightDown();
        await Task.Delay(ExchangeCollectClickHoldMs);
        Utility.Mouse.RightUp();
        await Task.Delay(ExchangeCollectClickHoldMs);
        // Released in a finally-free path on purpose: every caller awaits this, and leaving Ctrl
        // stuck down would turn the user's next ordinary click into a ctrl+click.
        Utility.Keyboard.KeyUp(Keys.LControlKey);
        await Task.Delay(ExchangeCollectClickHoldMs);
    }

    private static async Task<bool> WaitForExchangeCondition(Func<bool> condition, int timeoutMs)
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
                // Element rebuilt between frames; treat as "not yet".
            }

            if (stopwatch.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(ExchangeCollectPollMs);
        }
    }

    /// <summary>
    /// Sends everything in the inventory to the open stash tab, one ctrl+right-click at a time.
    /// Driven by the inventory count rather than by an assumption about how much a single click
    /// moves, so it is correct whether one click takes one stack or all of them. Returns false when
    /// items stopped moving, which in practice means the stash tab is full.
    /// </summary>
    private async Task<bool> DrainExchangeInventoryToStash(RectangleF panelRect)
    {
        var guard = 0;
        while (guard++ < ExchangeCollectMaxCycles)
        {
            var items = CurrentExchangeInventoryItems();
            var before = items?.Count ?? 0;
            if (before == 0) return true;

            var next = PickExchangeInventoryStack(items, panelRect);
            if (next == null)
            {
                _exchangeCollectStatus = $"{before} stack(s) stuck behind the exchange panel";
                LogError($"ExchangeCollect: {before} inventory stack(s) sit underneath the Currency " +
                         "Exchange panel and can't be clicked. Close the exchange and stash them by hand.");
                return false;
            }

            var rect = next.GetClientRectCache;
            if (rect.Width <= 0 || rect.Height <= 0) return false;

            // Retried before giving up: one click that doesn't register is a dropped input, not a
            // full stash, and treating it as fatal is how a run stops half way with no real reason.
            var moved = false;
            for (var attempt = 0; attempt < ExchangeCollectAttempts && !moved; attempt++)
            {
                await ExchangeCtrlRightClick(rect);

                // The list rebuilds on every move, so this re-reads the count rather than tracking
                // the item it clicked - that reference dies the moment the item lands.
                moved = await WaitForExchangeCondition(() => CurrentExchangeInventoryCount() < before,
                                                       ExchangeCollectMoveTimeoutMs);
            }

            if (!moved)
            {
                _exchangeCollectStatus = "stopped - stash tab full?";
                LogError("ExchangeCollect: an inventory stack wouldn't move to the stash after " +
                         $"{ExchangeCollectAttempts} attempts - the tab is probably full. Stopping so " +
                         "nothing is left half-collected silently.");
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Pulls every filled order into the stash: collect into the inventory, empty the inventory into
    /// the stash, repeat until the orders' Buying slots are empty.
    /// </summary>
    private async Task CollectFilledExchangeOrders()
    {
        var cycles = 0;
        var collected = 0;

        try
        {
            var startPanel = await FindExchangePanelWithRetry();
            if (startPanel == null)
            {
                _exchangeCollectStatus = "exchange panel closed";
                return;
            }

            // The stash is the destination, and opening the Currency Exchange doesn't open it. Do
            // this before anything else moves.
            _exchangeCollectStatus = "opening stash...";
            if (!await EnsureExchangeStashOpen(startPanel))
            {
                _exchangeCollectStatus = "couldn't open the stash";
                LogError("ExchangeCollect: the exchange panel's stash button didn't open the stash. " +
                         "Open it by hand and run again.");
                return;
            }

            // Anything already in the inventory goes first, or the first collect click has nowhere
            // to put things and silently does nothing.
            if (CurrentExchangeInventoryCount() > 0)
            {
                _exchangeCollectStatus = "clearing inventory...";
                if (!await DrainExchangeInventoryToStash(startPanel.GetClientRectCache))
                    return; // The drain has already set a status explaining why.
            }

            while (cycles++ < ExchangeCollectMaxCycles)
            {
                if (await ExchangeCollectCancelRequested())
                {
                    _exchangeCollectStatus = "cancelled";
                    LogMessage("ExchangeCollect: cancelled (right mouse held).");
                    return;
                }

                var panel = await FindExchangePanelWithRetry();
                if (panel == null)
                {
                    _exchangeCollectStatus = "exchange panel closed";
                    return;
                }

                var ready = CollectableExchangeOrders(panel);
                if (ready.Count == 0)
                {
                    _exchangeCollectStatus = collected == 0
                        ? "nothing to collect"
                        : $"done - {collected} pull(s) stashed";
                    LogMessage($"ExchangeCollect: finished after {collected} pull(s).");
                    return;
                }

                var row = ready[0];
                var slot = NavigateLowerPriceChildren(row, ExchangeOrderBuySlotIndex);
                if (slot == null)
                {
                    _exchangeCollectStatus = "couldn't read the order slot";
                    return;
                }

                var slotRect = slot.GetClientRectCache;
                if (slotRect.Width <= 0 || slotRect.Height <= 0)
                {
                    _exchangeCollectStatus = "order slot has no on-screen position";
                    return;
                }

                _exchangeCollectStatus = $"collecting {ExchangeOrderBuyingLabel(row) ?? "?"} " +
                                         $"({ready.Count} order(s) left)";

                var pulled = false;
                for (var attempt = 0; attempt < ExchangeCollectAttempts && !pulled; attempt++)
                {
                    await ExchangeCtrlRightClick(slotRect);
                    pulled = await WaitForExchangeCondition(() => CurrentExchangeInventoryCount() > 0,
                                                            ExchangeCollectMoveTimeoutMs);
                }

                if (!pulled)
                {
                    // The slot said it had items and nothing arrived twice over, so something about
                    // the interaction has changed. Stop rather than hammer an unresponsive slot.
                    _exchangeCollectStatus = $"nothing arrived in the inventory after {collected} pull(s)";
                    LogError("ExchangeCollect: ctrl+right-clicking the order slot put nothing in the " +
                             $"inventory after {ExchangeCollectAttempts} attempts. Stopping.");
                    return;
                }

                collected++;

                _exchangeCollectStatus = $"stashing (pull {collected})...";
                if (!await DrainExchangeInventoryToStash(panel.GetClientRectCache))
                    return; // The drain has already set a status explaining why.
            }

            _exchangeCollectStatus = $"stopped at the {ExchangeCollectMaxCycles}-cycle limit";
            LogError($"ExchangeCollect: hit the {ExchangeCollectMaxCycles}-cycle safety limit.");
        }
        catch (Exception ex)
        {
            _exchangeCollectStatus = "failed - see logs";
            LogError($"ExchangeCollect: run failed ({ex.Message}).");
        }
        finally
        {
            // Never leave Ctrl latched, whatever went wrong above.
            try { Utility.Keyboard.KeyUp(Keys.LControlKey); } catch { }
        }
    }

    /// <summary>Draws the collect button on the Currency Exchange panel, beside Place Order.</summary>
    partial void RenderExchangeCollect()
    {
        try
        {
            var panel = FindCurrencyExchangePanel();
            if (panel == null) return;

            // Anchored to the orders region rather than to the list itself, so the button keeps its
            // place when there are no orders and the list collapses to nothing.
            var region = NavigateLowerPriceChildren(panel, ExchangeOrdersRegionIndex);
            if (region == null) return;

            var anchor = region.GetClientRectCache;
            if (anchor.Width <= 0) return;

            var buttonRect = new RectangleF(anchor.X + ExchangeOrderListWidth + ExchangeCollectButtonGap,
                                            anchor.Y + 6f,
                                            ExchangeCollectButtonSize, ExchangeCollectButtonSize);

            var ready = CollectableExchangeOrders(panel).Count;

            // Green while there is something to collect, grey when there isn't - so the panel says
            // at a glance whether an order has filled, without opening anything.
            Graphics.DrawBox(buttonRect, ready > 0
                ? new SharpDX.Color(40, 140, 60, 220)
                : new SharpDX.Color(70, 70, 70, 160));
            Graphics.DrawFrame(buttonRect, SharpDX.Color.Black, 1);
            Graphics.DrawText(ready > 0 ? $"GET{ready}" : "GET",
                              new Vector2(buttonRect.X + 3, buttonRect.Y + 11),
                              ready > 0 ? SharpDX.Color.White : SharpDX.Color.Gray);

            var status = _exchangeCollectStatus;
            if (!string.IsNullOrEmpty(status))
                Graphics.DrawText(status, new Vector2(buttonRect.X, buttonRect.Y + buttonRect.Height + 4),
                                  SharpDX.Color.White);

            if (!IsExchangeCollectButtonPressed(buttonRect)) return;
            if (ready == 0)
            {
                _exchangeCollectStatus = "nothing to collect";
                return;
            }

            // One run at a time, or a second click starts a pass that fights the first for the mouse.
            if (Interlocked.Exchange(ref _exchangeCollectRunning, 1) == 1) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (Control.MouseButtons == MouseButtons.Left)
                        await Task.Delay(10);

                    await CollectFilledExchangeOrders();
                }
                catch (Exception ex)
                {
                    LogError($"ExchangeCollect: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _exchangeCollectRunning, 0);
                }
            });
        }
        catch (Exception ex)
        {
            LogError($"ExchangeCollect: render failed ({ex.Message}).");
        }
    }

    private bool IsExchangeCollectButtonPressed(RectangleF buttonRect)
    {
        try
        {
            var prevState = _exchangeCollectMouseState.GetValueOrDefault(buttonRect);
            var cursorPos = Utility.Mouse.GetCursorPosition();
            var windowPos = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
            var x = cursorPos.X - windowPos.X;
            var y = cursorPos.Y - windowPos.Y;

            var hovered = x >= buttonRect.X && x <= buttonRect.X + buttonRect.Width &&
                          y >= buttonRect.Y && y <= buttonRect.Y + buttonRect.Height;

            if (!hovered)
            {
                _exchangeCollectMouseState[buttonRect] = null;
                return false;
            }

            var pressed = Control.MouseButtons == MouseButtons.Left;
            _exchangeCollectMouseState[buttonRect] = pressed;
            return pressed && prevState == false;
        }
        catch
        {
            return false;
        }
    }
}
