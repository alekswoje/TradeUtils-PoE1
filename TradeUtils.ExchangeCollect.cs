using System;
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

    // The orders region and the stash button used to be reached by fixed child index - [20][2] and
    // [18]. Both moved: the panel's children now sit one index lower ([19][2] and [17]), which made
    // the collect button anchor to an INVISIBLE full-screen child at (632,108) and count zero
    // collectable orders with four sitting on screen. Nothing announced it; the button just went
    // grey and said "nothing to collect".
    //
    // So neither is looked up by index any more. Both are found by shape, the same way the panel
    // itself already was, and the indices below are only used to read INSIDE a row - which is a
    // layout GGG would have to redesign the widget to change.
    //
    // Inside an order row (11 children):
    //   [1] "Buying" label   [2] "Selling" label   [3] "Order Listed" / "Order Completed"
    //   [4] Buying slot (what you receive)         [5] Selling slot
    // and inside a slot: [0] the completion highlight, [1] the stack count.
    private const int ExchangeOrderRowChildCount = 11;
    private const int ExchangeOrderBuySlotIndex = 4;
    private const int ExchangeOrderBuyLabelIndex = 1;
    private const int ExchangeOrderStatusIndex = 3;
    private const int ExchangeSlotHighlightIndex = 0;
    private const int ExchangeSlotCountIndex = 1;
    private const string ExchangeOrderBuyLabel = "Buying";

    // Verified live: [4][0] is 90x90 over a 72x72 slot, and it is visible ONLY on a completed order
    // - it is the gold highlight frame, not the item icon it was documented as. That distinction
    // looks like it should matter and does NOT: an order only releases its contents once it is
    // fully complete, so "is this order completed" and "can this be collected" are the same
    // question, and the highlight answers it. See CollectableExchangeOrders.
    private const float ExchangeCollectButtonAboveList = 43f;
    private const float ExchangeCollectButtonInset = 4f;

    private const int ExchangeCollectClickHoldMs = 15;
    private const int ExchangeCollectPollMs = 8;
    // Moving a 50,000-strong stack can take the client a moment, and a timeout here reads as "the
    // stash is full" and stops the run - so it is generous, and every wait gets a second attempt.
    private const int ExchangeCollectMoveTimeoutMs = 2500;

    // Dropped synthetic clicks are routine, not exceptional, so failure is retried rather than
    // treated as a verdict: the same target a few times, then a different one, and only after a
    // sustained run of failures is anything called stuck.
    private const int ExchangeCollectAttempts = 3;
    private const int ExchangeCollectMaxDrainClicks = 200;
    private const int ExchangeCollectButtonSize = 37;

    // How many cycles in a row may pull currency into the inventory without getting any of it into
    // the stash before the run gives up.
    //
    // This is the guard against the "collecting into a bag that never empties" loop. A pull moves as
    // much as fits, so the first one usually fills the whole inventory - including the two columns
    // the exchange panel covers, which nothing can then click. Those covered cells are legitimately
    // undrainable, so a cycle that stashes nothing is not by itself wrong; a RUN of them means every
    // free cell left is a covered one and each further pull only buries more currency there. Three is
    // comfortably above what a healthy run does (in practice zero) and well below the 60-cycle limit,
    // which is what "it kept going with a full inventory" was previously running into.
    private const int ExchangeCollectMaxIdleCycles = 3;

    // How many times in a row the run may re-plan against a freshly-read order list before deciding
    // the list is not settling. Generous: orders vanishing as they empty is normal and each re-plan
    // is nearly free.
    private const int ExchangeCollectMaxReplans = 10;

    // A panel that reads as missing for a frame is a rebuild; one that stays missing is a panel the
    // user closed. Long enough to ride out the former, short enough that the latter stops the run
    // before the next click goes out.
    private const int ExchangePanelGraceMs = 350;

    // A run drives the real mouse, so it needs a way out that does not depend on the run itself
    // noticing anything is wrong: a watchdog, and a flag the render thread can set.
    //
    // This is an IDLE timeout, not a total-run budget. As a total it was the same mistake as the
    // cycle cap - it measured how long the work took rather than whether it was working, so a big
    // order that legitimately needs twenty inventory-loads got cut off at two minutes with currency
    // still in the order. Every pull that lands and every stack that reaches the stash pushes it
    // back, so a run that is getting somewhere runs as long as it needs to, and one that isn't stops
    // two minutes later at the outside - even if it has wedged somewhere with no idea it has.
    private const int ExchangeCollectIdleTimeoutMs = 120_000;

    // How long after a synthetic right-click the watcher keeps ignoring the right button.
    //
    // Both directions of this are a real cost, which is why it isn't just "generously large". Too
    // short and one of the run's own 15ms presses leaks through as a user cancel. Too long and it
    // swallows the user's: a drain clicks every few hundred milliseconds and each click re-arms the
    // mask, so a wide one leaves barely any unmasked gap for a quick click to land in. Ten times the
    // press it has to cover, which leaves most of each drain iteration watched. A right-click held
    // even briefly outlasts any mask and is always seen.
    private const int ExchangeSyntheticRightMaskMs = 150;

    // How far left of the panel the stash button is allowed to sit. Verified live it hangs off the
    // edge by ~100px (button at x=669, panel starts at x=767); the generous ceiling still excludes
    // an element sitting at the window origin, which is the entire point. See FindExchangeStashButton.
    private const float ExchangeStashButtonMaxLeftOffset = 300f;

    // null = not hovering the button, false = hovering with the mouse up, true = hovering pressed.
    private bool? _exchangeCollectButtonState;
    private int _exchangeCollectRunning; // 0 = idle, 1 = a collect run is in flight
    private volatile bool _exchangeCollectCancel;
    private DateTime _exchangeCollectDeadline = DateTime.MaxValue;
    private volatile string _exchangeCollectStatus;
    private int _exchangeCollectPulls; // orders emptied into the inventory this run

    // Set just before each synthetic right-click so the render-thread cancel watcher can tell the
    // run's own clicks from the user's. Ticks rather than DateTime so it can be written from the run
    // task and read from the render thread without tearing.
    private long _exchangeSyntheticRightUntilTicks;

    // Right button seen down, outside any synthetic click, since this moment. Render thread only.
    private DateTime? _exchangeUserRightDownSince;

    // Inventory stacks that have refused to move, by entity address, for the length of one run.
    //
    // Without this the drain had no memory: both its failure counter and its "try a different stack"
    // offset reset on every SUCCESSFUL move, and the picker always walks the inventory from the end -
    // so the one stack the stash won't take got picked again immediately after every good click. That
    // is three more 2.5s timeouts before each stack that actually moves, and because the counter kept
    // resetting the run never reached the failure limit that would have stopped it. It just ground on
    // at roughly one stack per eight seconds until the wall-clock budget killed it, which is what
    // "it keeps failing and retrying" looks like from outside.
    private readonly HashSet<long> _exchangeStuckStacks = new HashSet<long>();

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

    /// <summary>True for an element shaped like one placed order.</summary>
    private static bool LooksLikeExchangeOrderRow(Element candidate)
    {
        try
        {
            if (candidate == null || candidate.ChildCount != ExchangeOrderRowChildCount) return false;

            var slot = candidate.Children[ExchangeOrderBuySlotIndex];
            if (slot == null || slot.ChildCount != 2) return false;

            var label = candidate.Children[ExchangeOrderBuyLabelIndex]?.Text;
            return label != null &&
                   label.Trim().Equals(ExchangeOrderBuyLabel, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The element whose children are the placed orders, found by looking for a container whose
    /// first child is shaped like an order row. Two levels deep is enough - the list lives at
    /// panel -> orders region -> list - and the row signature ("Buying" label plus a two-part slot)
    /// is specific enough that exactly one container matched when this was checked live.
    ///
    /// Returns null when there are no orders at all, which is indistinguishable from a layout
    /// change and is treated the same way: nothing to collect.
    /// </summary>
    private Element FindExchangeOrderList(Element panel)
    {
        if (panel == null) return null;

        try
        {
            for (var i = 0; i < panel.ChildCount; i++)
            {
                var region = panel.Children[i];
                if (region == null) continue;

                for (var j = 0; j < region.ChildCount; j++)
                {
                    var candidate = region.Children[j];

                    try
                    {
                        if (candidate == null || !candidate.IsVisibleLocal || candidate.ChildCount == 0)
                            continue;
                        if (!LooksLikeExchangeOrderRow(candidate.Children[0])) continue;
                    }
                    catch
                    {
                        continue;
                    }

                    return candidate;
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"ExchangeCollect: couldn't look for the order list ({ex.Message}).");
        }

        return null;
    }

    /// <summary>
    /// The panel's stash button - the chest icon that hangs off the panel's LEFT edge, which is
    /// what makes it findable: it is the only child of the panel that sits outside the panel's own
    /// left boundary. Everything else the panel draws is inside it.
    ///
    /// "Outside the left boundary" on its own is not enough, and this is the bug that made the
    /// collector click the top-left corner of the screen. An element the client has not laid out
    /// reports its rect at the window ORIGIN - which passes "ends before the panel's left edge" more
    /// convincingly than the real button does, has a plausible width and height, and can appear at a
    /// LOWER child index, so it won the first-match-wins search. The run then left-clicked (0,0),
    /// waited 2.5s for a stash that was never going to open, and gave up.
    ///
    /// It only ever showed up when the stash was CLOSED, because that is the only time this runs -
    /// which is exactly the "it breaks when the inventory isn't already open" shape of the report.
    ///
    /// So the test is now positive on both axes - the button must hang off the left edge but stay
    /// NEAR it, and must overlap the panel vertically - and the closest candidate wins rather than
    /// the first one found.
    /// </summary>
    private Element FindExchangeStashButton(Element panel)
    {
        if (panel == null) return null;

        try
        {
            var panelRect = panel.GetClientRectCache;
            if (panelRect.Width <= 0 || panelRect.Height <= 0) return null;

            Element best = null;
            var bestGap = float.MaxValue;

            for (var i = 0; i < panel.ChildCount; i++)
            {
                var candidate = panel.Children[i];

                try
                {
                    if (candidate == null || !candidate.IsVisibleLocal || candidate.ChildCount != 1)
                        continue;

                    var rect = candidate.GetClientRectCache;
                    if (rect.Width < 40 || rect.Width > 160 || rect.Height < 40) continue;

                    // Ends at or before the panel's left edge, i.e. hanging off it...
                    var gap = panelRect.X + 20 - (rect.X + rect.Width);
                    if (gap < 0) continue;

                    // ...but still beside it, not adrift somewhere else on screen.
                    if (gap > ExchangeStashButtonMaxLeftOffset) continue;

                    // ...and level with it. An origin-parked element fails this outright.
                    var centreY = rect.Y + rect.Height / 2f;
                    if (centreY < panelRect.Y || centreY > panelRect.Y + panelRect.Height) continue;

                    if (gap >= bestGap) continue;

                    bestGap = gap;
                    best = candidate;
                }
                catch
                {
                    // Torn down between frames; keep looking.
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            LogError($"ExchangeCollect: couldn't look for the stash button ({ex.Message}).");
        }

        return null;
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

        var button = FindExchangeStashButton(panel);
        if (button == null)
        {
            LogError("ExchangeCollect: couldn't find the panel's stash button. Open the stash by hand " +
                     "and run again.");
            return false;
        }

        var rect = button.GetClientRectCache;
        if (!IsSaneExchangeClickTarget(rect, panel.GetClientRectCache, out var reason))
        {
            LogError($"ExchangeCollect: the element that looks like the stash button is at " +
                     $"({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}) - {reason}. Not " +
                     "clicking it. Open the stash by hand and run again.");
            return false;
        }

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

    private bool IsExchangeInventoryOpen()
    {
        try { return GameController?.IngameState?.IngameUi?.InventoryPanel?.IsVisible == true; }
        catch { return false; }
    }

    /// <summary>What a click needs to be true before it goes out, and the panel it aims relative to.</summary>
    private readonly struct ExchangeReadyState
    {
        public ExchangeReadyState(Element panel, RectangleF panelRect, string missing)
        {
            Panel = panel;
            PanelRect = panelRect;
            Missing = missing;
        }

        public Element Panel { get; }
        public RectangleF PanelRect { get; }

        /// <summary>Null when everything is in place; otherwise what isn't, phrased for the overlay.</summary>
        public string Missing { get; }

        public bool Ok => Missing == null;

        public static ExchangeReadyState Blocked(string missing) =>
            new ExchangeReadyState(null, default, missing);
    }

    /// <summary>
    /// Everything that has to hold before this feature is allowed to press a mouse button, re-read
    /// from the client each time rather than assumed from a check made earlier in the run.
    ///
    /// **The Currency Exchange being open is part of it, and that is the fix for the worst failure
    /// this feature had.** The panel used to be resolved once per cycle and its rect carried through
    /// every click that followed - up to three attempts on each of several orders, each waiting 2.5s,
    /// so half a minute of clicking on the strength of one lookup. Close the exchange inside that
    /// window and nothing noticed: the order rows still report their last rect, the "is this a sane
    /// place to click" test compares them against the stale panel rect they were laid out inside, and
    /// they pass it perfectly. The ctrl+right-clicks then land on the game world where the panel used
    /// to be, which in a hideout means the character runs off and attacks things. Checking here means
    /// a closed panel stops the run instead - and makes Escape a panic button that works.
    ///
    /// The other three are older but the same shape. With the stash closed, ctrl+right-clicking an
    /// inventory stack does nothing, so the drain can only read it as a stack that won't move. With
    /// the inventory closed it is worse: VisibleInventoryItems reads EMPTY rather than failing, which
    /// is indistinguishable from "everything got stashed", so the loop pulls into a bag it can't see.
    /// And with the game not focused, synthetic clicks go to whatever the user alt-tabbed to.
    /// </summary>
    private async Task<ExchangeReadyState> ExchangeReadyToClick()
    {
        bool focused;
        try { focused = GameController.Window.IsForeground(); } catch { focused = true; }
        if (!focused) return ExchangeReadyState.Blocked("the game window isn't focused");

        // Retried briefly: a single missing frame while the UI rebuilds is not the user closing the
        // panel, and treating it as one was already the most likely reason a run stopped part-way.
        var panel = await FindExchangePanelWithRetry(ExchangePanelGraceMs);
        if (panel == null) return ExchangeReadyState.Blocked("the Currency Exchange is closed");

        RectangleF panelRect;
        try { panelRect = panel.GetClientRectCache; }
        catch { return ExchangeReadyState.Blocked("the exchange panel can't be measured"); }

        if (panelRect.Width <= 0 || panelRect.Height <= 0)
            return ExchangeReadyState.Blocked("the exchange panel has no rect");

        if (!IsExchangeStashOpen() && !IsExchangeInventoryOpen())
            return ExchangeReadyState.Blocked("the stash and inventory are closed");
        if (!IsExchangeStashOpen()) return ExchangeReadyState.Blocked("the stash is closed");
        if (!IsExchangeInventoryOpen()) return ExchangeReadyState.Blocked("the inventory is closed");

        return new ExchangeReadyState(panel, panelRect, null);
    }

    /// <summary>Records why a run stopped, in the one place the user is actually looking.</summary>
    private void StopExchangeRun(string missing, string detail = null)
    {
        _exchangeCollectStatus = $"stopped - {missing}";
        LogError($"ExchangeCollect: {missing}. {detail ?? "Stopping rather than clicking at something that isn't there."}");
    }

    /// <summary>
    /// True when an order's Buying slot holds something. Only meaningful alongside the completion
    /// check - a part-filled order shows a stack it will not let go of.
    ///
    /// The count is abbreviated once it gets large ("54.7K"), so it is never parsed as a number;
    /// only "is any of this a non-zero digit" is asked of it, which no abbreviation changes.
    /// </summary>
    private static bool ExchangeSlotHasStock(Element row)
    {
        try
        {
            var text = row?.Children[ExchangeOrderBuySlotIndex]?.Children[ExchangeSlotCountIndex]?.Text;
            if (string.IsNullOrWhiteSpace(text)) return false;

            foreach (var c in text)
                if (c >= '1' && c <= '9') return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True while the Buying slot carries the gold "this order is done" highlight.</summary>
    private static bool IsExchangeOrderCompleted(Element row)
    {
        try
        {
            return row?.Children[ExchangeOrderBuySlotIndex]
                      ?.Children[ExchangeSlotHighlightIndex]?.IsVisibleLocal == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Order rows that can actually be collected.
    ///
    /// **A part-filled order cannot be collected at all.** Path of Exile releases nothing until the
    /// order is FULLY complete, so an order showing 14 of 100 bought is holding those 14 where no
    /// amount of ctrl+right-clicking will reach them. That is a game rule, not a UI quirk, and it is
    /// why the completion highlight - not the stack count - decides what goes in this list. Reading
    /// the count as "there's something in there, go get it" sent the collector after currency it
    /// could never take, which is exactly what it did until this was corrected.
    ///
    /// The count is still checked, as a second condition rather than the first: it guards against
    /// clicking at a completed order that has already been emptied.
    /// </summary>
    private List<Element> CollectableExchangeOrders(Element panel) =>
        CollectableExchangeOrdersIn(FindExchangeOrderList(panel));

    /// <inheritdoc cref="CollectableExchangeOrders(Element)"/>
    /// <param name="list">
    /// An already-resolved order list, for callers that have one. The render path runs every frame
    /// and used to find the list twice per frame - once for the button's anchor and once in here.
    /// </param>
    private List<Element> CollectableExchangeOrdersIn(Element list)
    {
        var ready = new List<Element>();

        if (list == null) return ready;

        IList<Element> rows;
        try { rows = list.Children; } catch { return ready; }
        if (rows == null) return ready;

        foreach (var row in rows)
        {
            if (row == null) continue;
            if (!IsExchangeOrderCompleted(row)) continue;
            if (!ExchangeSlotHasStock(row)) continue;

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
    /// stacks furthest from the panel first. Returns null when nothing is reachable, which the
    /// caller reports instead of clicking blind into the panel.
    ///
    /// A stack has to sit entirely RIGHT of the panel to qualify, which is stricter than the "does
    /// not overlap it" test this used to apply, and deliberately so. PoE docks the inventory to the
    /// right edge of the screen and the exchange sits inboard of it - measured live, grid x1695-2538
    /// against panel x767-1793 - so a real cell is always right of the panel and never left of it.
    /// An inventory the client has NOT laid out reports its cells around the window origin, which
    /// clears the panel on the left and passed the old test cleanly; the click then landed in the
    /// top-left corner of the screen. Nothing legitimate is over there, so nothing over there is
    /// clicked.
    /// </summary>
    /// <param name="stuck">
    /// Stacks already established as immovable this run, skipped rather than tried again. Without
    /// this the picker hands back the same refusing stack after every successful click, because it
    /// stays put while everything around it leaves and the walk starts from the end.
    /// </param>
    private static NormalInventoryItem PickExchangeInventoryStack(IList<NormalInventoryItem> items,
                                                                  RectangleF panelRect,
                                                                  ICollection<long> stuck)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item == null) continue;

            RectangleF rect;
            try { rect = item.GetClientRectCache; } catch { continue; }
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            if (rect.X < panelRect.X + panelRect.Width) continue;

            if (stuck != null && stuck.Contains(ExchangeStackAddress(item))) continue;

            return item;
        }

        return null;
    }

    /// <summary>
    /// Identity for one inventory stack, stable across the list rebuilding underneath it - which it
    /// does constantly, so the element reference can't be held and the grid position can't be
    /// trusted (InventPosX/Y are obsolete and read 0,0 in the panels this plugin drives).
    /// </summary>
    private static long ExchangeStackAddress(NormalInventoryItem item)
    {
        try { return item?.Item?.Address ?? 0; } catch { return 0; }
    }

    /// <summary>
    /// The size of the stack at <paramref name="address"/>, or -1 once it has left the inventory.
    ///
    /// Whether a click worked can't be answered by the item count alone. Sending a stack into a stash
    /// tab that already holds a partial one of the same currency splits it: some moves, the rest stays
    /// put, and the count doesn't budge. Read as a failure that is a stack heading for the immovable
    /// list while it is in fact draining perfectly well. Returns <paramref name="fallback"/> when the
    /// inventory can't be read at all, so an unreadable frame counts as "no news" rather than progress.
    /// </summary>
    private int ExchangeStackSizeOf(long address, int fallback)
    {
        var items = CurrentExchangeInventoryItems();
        if (items == null) return fallback;

        foreach (var item in items)
        {
            try
            {
                var entity = item?.Item;
                if (entity == null || entity.Address != address) continue;

                return entity.GetComponent<ExileCore.PoEMemory.Components.Stack>()?.Size ?? 1;
            }
            catch
            {
                // This slot rebuilt mid-read; the others are still worth checking.
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether the inventory is drawn where an inventory is drawn - i.e. whether these rects came
    /// from a laid-out grid at all.
    ///
    /// "Under the exchange panel" and "not on screen anywhere sensible" both leave the picker with
    /// nothing to click, and they need opposite responses: the first is normal and the run should
    /// carry on, the second means every rect is fiction and the run has to stop. One cell overlapping
    /// the panel is enough to tell them apart, because a covered cell is a cell that IS placed.
    /// </summary>
    private static bool ExchangeInventoryLooksPlaced(IList<NormalInventoryItem> items,
                                                     RectangleF panelRect)
    {
        foreach (var item in items)
        {
            if (item == null) continue;

            RectangleF rect;
            try { rect = item.GetClientRectCache; } catch { continue; }
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            // Reaches the panel's left edge or beyond, i.e. it is over on the inventory's side of
            // the screen. Deliberately the loosest test that an origin-parked grid still fails -
            // a false "not placed" would break the feature outright, so it only has to rule out
            // rects that are nowhere near where the inventory lives.
            if (rect.X + rect.Width > panelRect.X) return true;
        }

        return false;
    }

    /// <summary>
    /// Marks the next few hundred milliseconds as "that right-click was ours". Called immediately
    /// before every synthetic right press.
    /// </summary>
    private void MaskExchangeSyntheticRightClick() =>
        Interlocked.Exchange(ref _exchangeSyntheticRightUntilTicks,
                             DateTime.UtcNow.AddMilliseconds(ExchangeSyntheticRightMaskMs).Ticks);

    private bool ExchangeSyntheticRightActive() =>
        DateTime.UtcNow.Ticks < Interlocked.Read(ref _exchangeSyntheticRightUntilTicks);

    /// <summary>
    /// Stops the run when the user right-clicks, polled from the render thread every frame.
    ///
    /// Right-click is the obvious way to want out of this - it is what the mouse is already doing and
    /// the run has the cursor, so reaching a button is awkward. The difficulty is that the action
    /// this feature performs IS a right-click, so the naive check cancels the run on its own input.
    ///
    /// It used to be answered with duration: sample, wait 60ms, sample again, and call it the user's
    /// if the button was still down. That has both failure modes at once. It ran on the run's own
    /// task, once per outer cycle, so for the whole of a drain - the longest part, up to 200 clicks -
    /// there was nothing to right-click AT; and a real click that happened to overlap one of ours
    /// still read as ours.
    ///
    /// Masking the run's own clicks instead answers the actual question, and answers it from the
    /// render thread where it can be asked every frame. Our press is 15ms once every couple of
    /// seconds and announces itself beforehand, so a right button down at any other moment is the
    /// user's and one frame of it is enough - no hold required.
    /// </summary>
    private void WatchForExchangeCollectRightClickCancel()
    {
        try
        {
            bool down;
            try { down = (Control.MouseButtons & MouseButtons.Right) != 0; } catch { return; }

            if (!down || ExchangeSyntheticRightActive())
            {
                _exchangeUserRightDownSince = null;
                return;
            }

            // A run started by a click that arrived with right already held - or a right-click the
            // mask expired underneath - shouldn't cancel on the press that was already down when the
            // watch began. Requiring one frame of separation costs nothing a user would notice.
            if (_exchangeUserRightDownSince == null)
            {
                _exchangeUserRightDownSince = DateTime.UtcNow;
                return;
            }

            if (_exchangeCollectCancel) return;

            _exchangeCollectCancel = true;
            _exchangeCollectStatus = "stopping (right-click)...";
            LogMessage("ExchangeCollect: stopping - you right-clicked.");
        }
        catch
        {
            // The watcher must never be the thing that throws out of render.
        }
    }

    /// <summary>
    /// Whether a rect is somewhere this feature is ever allowed to click.
    ///
    /// Every target here is a rect read out of the element tree, and an element the client has not
    /// laid out reports one at the window ORIGIN with a perfectly plausible width and height. A
    /// "Width > 0" guard waves that straight through, and the click lands in the top-left corner of
    /// the screen on whatever happens to be there - in a hideout, that is the ground, so the
    /// character walks off. That is the failure this exists to stop, and it is why the check is
    /// about WHERE a rect is and not just whether it has a size.
    ///
    /// The Currency Exchange panel is the reference frame, because it is the one element that is
    /// found by shape and was verified against the live client. Everything this feature legitimately
    /// clicks is either inside the panel, just off its left edge (the stash button), or in the
    /// inventory to its right. Nothing sits above it, and nothing sits at the origin.
    /// </summary>
    private bool IsSaneExchangeClickTarget(RectangleF rect, RectangleF panelRect, out string reason)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            reason = "it has no size";
            return false;
        }

        RectangleF window;
        try { window = GameController.Window.GetWindowRectangleTimeCache; }
        catch (Exception ex)
        {
            reason = $"the window rect is unreadable ({ex.Message})";
            return false;
        }

        // GetClientRectCache is window-relative, so the window's own origin is (0,0) here.
        if (rect.X < 0 || rect.Y < 0 ||
            rect.X + rect.Width > window.Width || rect.Y + rect.Height > window.Height)
        {
            reason = $"it falls outside the {window.Width}x{window.Height} game window";
            return false;
        }

        if (panelRect.Width <= 0 || panelRect.Height <= 0)
        {
            reason = "the exchange panel has no rect to check it against";
            return false;
        }

        var centre = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);

        if (centre.Y < panelRect.Y)
        {
            reason = $"it sits above the exchange panel (y {centre.Y:0} < {panelRect.Y:0})";
            return false;
        }

        if (centre.X < panelRect.X - ExchangeStashButtonMaxLeftOffset)
        {
            reason = $"it sits left of the exchange panel (x {centre.X:0} < " +
                     $"{panelRect.X - ExchangeStashButtonMaxLeftOffset:0})";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Ctrl+right-clicks <paramref name="rect"/>, or refuses and says why. Every click this feature
    /// makes goes through here so that no single call site can be the one that forgets to look.
    /// </summary>
    private async Task<bool> ExchangeCtrlRightClickChecked(RectangleF rect, RectangleF panelRect,
                                                           string what)
    {
        if (!IsSaneExchangeClickTarget(rect, panelRect, out var reason))
        {
            LogError($"ExchangeCollect: refusing to click {what} at " +
                     $"({rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}) - {reason}. This is the " +
                     "client reporting a rect for something it hasn't laid out; nothing was clicked.");
            return false;
        }

        await ExchangeCtrlRightClick(rect);
        return true;
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

        // Declared before the press, never after: the render thread samples the button every frame
        // and would otherwise catch our own click in the gap and stop the run.
        MaskExchangeSyntheticRightClick();

        Utility.Mouse.RightDown();
        await Task.Delay(ExchangeCollectClickHoldMs);
        Utility.Mouse.RightUp();
        MaskExchangeSyntheticRightClick();
        await Task.Delay(ExchangeCollectClickHoldMs);
        // Released in a finally-free path on purpose: every caller awaits this, and leaving Ctrl
        // stuck down would turn the user's next ordinary click into a ctrl+click.
        Utility.Keyboard.KeyUp(Keys.LControlKey);
        await Task.Delay(ExchangeCollectClickHoldMs);
    }

    /// <param name="requirePanel">
    /// Give up early if the Currency Exchange closes. Every wait here is the tail of a click, so a
    /// wait that runs on after the panel has gone is 2.5s of the run believing it is mid-interaction
    /// with a panel the user has already shut - and then carrying on to the next click.
    /// </param>
    private async Task<bool> WaitForExchangeCondition(Func<bool> condition, int timeoutMs,
                                                      bool requirePanel = false)
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

            // A cancel that has to wait out a 2.5s timeout - times however many are still queued
            // behind it - is not a cancel. Bail the moment one is asked for.
            if (ExchangeCollectStopping()) return false;

            // Cheap: the lookup is cached and the cache invalidates itself the moment the panel
            // stops being visible, so this costs a visibility read per poll and a single rescan on
            // the way out. The caller re-checks properly - with the grace period - before it acts on
            // this, so one flickery frame here only ends a wait early.
            if (requirePanel && FindCurrencyExchangePanel() == null) return false;

            if (stopwatch.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(ExchangeCollectPollMs);
        }
    }

    /// <summary>
    /// True once the run has been told to stop, or has used up its wall-clock budget. Cheap and
    /// non-blocking on purpose: it is checked inside every loop, including the drain, so that no
    /// stretch of a run is unreachable.
    /// </summary>
    private bool ExchangeCollectStopping() =>
        _exchangeCollectCancel || DateTime.UtcNow > _exchangeCollectDeadline;

    /// <summary>
    /// Something moved. Pushes the idle watchdog back, so the timeout measures how long the run has
    /// been getting nowhere rather than how long it has been going.
    /// </summary>
    private void TouchExchangeCollectProgress() =>
        _exchangeCollectDeadline = DateTime.UtcNow.AddMilliseconds(ExchangeCollectIdleTimeoutMs);

    /// <summary>
    /// Reports a stop and sets the status, so a caller only has to ask once per loop.
    /// </summary>
    private bool ExchangeCollectStopped()
    {
        if (!ExchangeCollectStopping()) return false;

        // Read off a field rather than passed in, so the drain - which has no idea how many pulls the
        // run has made, only how many stacks it just shifted - reports the same number as everywhere
        // else instead of its own.
        var collected = _exchangeCollectPulls;

        if (_exchangeCollectCancel)
        {
            _exchangeCollectStatus = $"stopped - {collected} pull(s) stashed";
            LogMessage($"ExchangeCollect: stopped on request after {collected} pull(s).");
        }
        else
        {
            _exchangeCollectStatus = $"stalled - {collected} pull(s) stashed";
            LogError($"ExchangeCollect: nothing has moved for {ExchangeCollectIdleTimeoutMs / 1000}s " +
                     $"after {collected} pull(s), so the run is wedged on something it can't see. " +
                     "Press GET again to carry on.");
        }

        return true;
    }

    /// <summary>How a drain pass ended.</summary>
    private enum ExchangeDrainResult
    {
        /// <summary>Inventory is empty.</summary>
        Emptied,

        /// <summary>Everything reachable was sent; what's left sits under the exchange panel.</summary>
        BlockedByPanel,

        /// <summary>A reachable stack refused to move - in practice a full stash tab.</summary>
        Stuck,
    }

    /// <summary>
    /// How a drain pass ended, and how much it actually shifted.
    ///
    /// The count is the part the caller needs and used to be missing. "Blocked by the panel" says
    /// nothing about whether the pass achieved anything: it is the normal ending for a pass that
    /// stashed fifty stacks and left two in the covered columns, and also for a pass that stashed
    /// nothing because the covered columns are all that's left. The first should carry on, the second
    /// must not - pulling again only buries more currency where nothing can reach it.
    /// </summary>
    private readonly struct ExchangeDrainOutcome
    {
        public ExchangeDrainOutcome(ExchangeDrainResult result, int stashed)
        {
            Result = result;
            Stashed = stashed;
        }

        public ExchangeDrainResult Result { get; }

        /// <summary>Stacks that reached the stash this pass.</summary>
        public int Stashed { get; }
    }

    /// <summary>
    /// Sends everything reachable in the inventory to the open stash tab, one ctrl+right-click at a
    /// time. Driven by the inventory count rather than by an assumption about how much a single
    /// click moves, so it is correct whether one click takes one stack or all of them.
    ///
    /// Measured live: the inventory grid runs x1695-2538 and the exchange panel x767-1793, so the
    /// first two columns - 10 of 60 cells - are underneath it and cannot be clicked at all while the
    /// exchange is open. That is a permanent feature of the layout, not an error, so stacks landing
    /// there are reported and stepped over rather than ending the run.
    /// </summary>
    /// <remarks>
    /// A click that doesn't register is the normal case, not the exceptional one - synthetic input
    /// gets dropped, the client eats one during a frame hitch, the cursor lands mid-rebuild. So a
    /// failure retries the SAME stack a few times, then moves on to a DIFFERENT one, and only calls
    /// the whole thing stuck once nothing reachable will move at all. Each attempt re-reads the item
    /// list and re-picks its target, because the panel rebuilds constantly and a rect captured
    /// before a rebuild points at nothing.
    /// </remarks>
    private async Task<ExchangeDrainOutcome> DrainExchangeInventoryToStash()
    {
        var clicks = 0;
        var stashed = 0;

        long currentAddress = 0;   // the stack being worked on
        var attemptsOnCurrent = 0;

        while (clicks++ < ExchangeCollectMaxDrainClicks)
        {
            // The drain is the longest stretch of a run - 200 clicks with a 2.5s timeout each - and
            // it used to be completely unreachable: the only cancel lived in the outer loop.
            if (ExchangeCollectStopping())
            {
                ExchangeCollectStopped();
                return new ExchangeDrainOutcome(ExchangeDrainResult.Stuck, stashed);
            }

            // Re-checked every click, not just on entry: the exchange, the stash or the inventory can
            // all be closed part-way through, and each one turns the next click into something other
            // than what this code thinks it is doing. The panel rect comes from here too, freshly, so
            // the click is aimed relative to where the panel is NOW.
            var ready = await ExchangeReadyToClick();
            if (!ready.Ok)
            {
                StopExchangeRun(ready.Missing);
                return new ExchangeDrainOutcome(ExchangeDrainResult.Stuck, stashed);
            }

            var panelRect = ready.PanelRect;

            var items = CurrentExchangeInventoryItems();
            var before = items?.Count ?? 0;
            if (before == 0) return new ExchangeDrainOutcome(ExchangeDrainResult.Emptied, stashed);

            var next = PickExchangeInventoryStack(items, panelRect, _exchangeStuckStacks);
            if (next == null)
            {
                // Nothing left to aim at, and which of the two reasons decides whether the run has a
                // future: stacks parked under the panel are a layout fact the caller works around,
                // stacks the stash refused are the end of the road.
                if (_exchangeStuckStacks.Count > 0)
                {
                    _exchangeCollectStatus = $"stopped - stash tab won't take {_exchangeStuckStacks.Count} stack(s)";
                    LogError($"ExchangeCollect: {_exchangeStuckStacks.Count} inventory stack(s) refused " +
                             $"to move after {ExchangeCollectAttempts} attempts each, and nothing else " +
                             "is reachable - the stash tab is full, or it has no room for what's left. " +
                             "Make space and press GET again.");
                    return new ExchangeDrainOutcome(ExchangeDrainResult.Stuck, stashed);
                }

                // ...unless the grid isn't drawn where a grid goes at all, in which case this is
                // not a covered corner, it is an inventory the client hasn't laid out. Telling
                // the two apart matters: "covered" tells the caller to pull more, and pulling
                // more into an inventory nobody can read is the loop that never gets anywhere.
                if (!ExchangeInventoryLooksPlaced(items, panelRect))
                {
                    _exchangeCollectStatus = "stopped - can't locate the inventory";
                    LogError($"ExchangeCollect: the inventory reports {before} stack(s) but none of " +
                             "them are drawn anywhere a cell can be. The client hasn't laid the " +
                             "inventory out, so nothing here can be clicked safely - a click would " +
                             "land in the top-left corner of the screen. Open the inventory and " +
                             "run again.");
                    return new ExchangeDrainOutcome(ExchangeDrainResult.Stuck, stashed);
                }

                LogMessage($"ExchangeCollect: {before} inventory stack(s) sit under the Currency " +
                           "Exchange panel and can't be clicked; leaving them and carrying on.");
                return new ExchangeDrainOutcome(ExchangeDrainResult.BlockedByPanel, stashed);
            }

            var address = ExchangeStackAddress(next);
            if (address != currentAddress)
            {
                currentAddress = address;
                attemptsOnCurrent = 0;
            }

            RectangleF rect;
            try { rect = next.GetClientRectCache; } catch { rect = default; }
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                if (!MarkExchangeStackStuck(address, "it has no rect to click"))
                    return ExchangeDrainUnidentifiable(stashed);
                continue;
            }

            // The whole grid shares a parent, so one stack reporting a nonsense position means the
            // client has not laid the inventory out and every other stack is reporting nonsense too.
            // Stepping over them one at a time would just spend the click budget clicking rubbish -
            // and the rubbish is a rect at the window origin, i.e. the top-left corner of the screen.
            if (!IsSaneExchangeClickTarget(rect, panelRect, out var why))
            {
                _exchangeCollectStatus = "stopped - can't locate the inventory";
                LogError($"ExchangeCollect: the inventory reports {before} stack(s) but one of them " +
                         $"is at ({rect.X:0},{rect.Y:0}) - {why}. The client hasn't laid the inventory " +
                         "out, so nothing here can be clicked safely. Open the inventory and run again.");
                return new ExchangeDrainOutcome(ExchangeDrainResult.Stuck, stashed);
            }

            // Read before the click so a part-move can be recognised as the progress it is.
            var sizeBefore = address == 0 ? 0 : ExchangeStackSizeOf(address, 0);

            await ExchangeCtrlRightClick(rect);

            // Never holds the item reference across the wait: the list rebuilds the moment anything
            // lands and that reference dies with it. Identity travels as an address instead.
            var moved = await WaitForExchangeCondition(
                () => CurrentExchangeInventoryCount() < before ||
                      (address != 0 && ExchangeStackSizeOf(address, sizeBefore) < sizeBefore),
                ExchangeCollectMoveTimeoutMs, requirePanel: true);

            if (moved)
            {
                stashed++;
                TouchExchangeCollectProgress();
                currentAddress = 0;
                attemptsOnCurrent = 0;
                continue;
            }

            // A dropped synthetic click is routine, so the same stack is worth a few goes. What is
            // NOT routine is coming back to it after it has had them: once a stack has spent its
            // attempts it is set aside for the rest of the run, and the picker stops handing it back.
            if (++attemptsOnCurrent >= ExchangeCollectAttempts)
            {
                if (!MarkExchangeStackStuck(address, $"it didn't move in {attemptsOnCurrent} attempts"))
                    return ExchangeDrainUnidentifiable(stashed);

                currentAddress = 0;
                attemptsOnCurrent = 0;
            }

            // Park the cursor off-target so the next click arrives as a fresh hover rather than a
            // second press on a cell the client already thinks is being interacted with.
            await NudgeExchangeCursorAway(rect);
        }

        _exchangeCollectStatus = $"stopped - {ExchangeCollectMaxDrainClicks} clicks without emptying";
        LogError($"ExchangeCollect: spent the {ExchangeCollectMaxDrainClicks}-click drain budget " +
                 $"without emptying the inventory ({stashed} stack(s) stashed). Stopping.");
        return new ExchangeDrainOutcome(ExchangeDrainResult.Stuck, stashed);
    }

    /// <summary>
    /// Sets a stack aside for the rest of the run. False when it has no readable address, i.e. when
    /// it cannot be set aside - which the caller has to treat as fatal rather than shrug at, because
    /// the picker will hand back the very same stack on the next pass and the loop that produced is
    /// exactly the one this whole mechanism exists to break.
    /// </summary>
    private bool MarkExchangeStackStuck(long address, string why)
    {
        if (address == 0) return false;

        if (_exchangeStuckStacks.Add(address))
            LogMessage($"ExchangeCollect: leaving one inventory stack alone - {why}. " +
                       $"{_exchangeStuckStacks.Count} set aside so far.");

        return true;
    }

    /// <summary>Ends a drain that met a stack it can't identify, and therefore can't skip.</summary>
    private ExchangeDrainOutcome ExchangeDrainUnidentifiable(int stashed)
    {
        _exchangeCollectStatus = "stopped - can't read the inventory";
        LogError("ExchangeCollect: an inventory stack won't move and has no readable entity behind " +
                 "it, so it can't be told apart from the next one and would be retried forever. " +
                 "Stopping. Reopen the inventory and press GET again.");
        return new ExchangeDrainOutcome(ExchangeDrainResult.Stuck, stashed);
    }

    /// <summary>Moves the cursor clear of <paramref name="rect"/> and lets the UI settle.</summary>
    private async Task NudgeExchangeCursorAway(RectangleF rect)
    {
        try
        {
            var windowTopLeft = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
            Utility.Mouse.moveMouse(new Vector2(windowTopLeft.X + rect.X + rect.Width / 2f,
                                                windowTopLeft.Y + rect.Y + rect.Height + 24f));
        }
        catch
        {
            // Cursor parking is best-effort; the retry is worth making either way.
        }

        await Task.Delay(80);
    }

    /// <summary>How an attempt to empty one order's Buying slot into the inventory ended.</summary>
    private enum ExchangePullResult
    {
        /// <summary>Currency arrived in the inventory.</summary>
        Pulled,

        /// <summary>The order was clicked and gave nothing back.</summary>
        NothingArrived,

        /// <summary>
        /// The order isn't in the list any more - collected, cancelled, or the list rebuilt shorter.
        /// Not a failure, and it must not be counted as one: an emptied order disappearing is the
        /// normal way this feature finishes.
        /// </summary>
        OrderGone,

        /// <summary>Something needed for the click stopped being true. The run is over.</summary>
        Aborted,
    }

    /// <summary>
    /// Ctrl+right-clicks the <paramref name="index"/>th collectable order, retrying a few times.
    ///
    /// Everything is re-resolved on every attempt - the panel, its rect, the order list, the row and
    /// the slot - and that is the point of the method existing. The old code walked a list of Element
    /// references captured once per cycle and clicked rects read off them minutes later. Two things
    /// go wrong with that. The panel rebuilds its rows whenever a listing changes, so a captured row
    /// is an address with nothing behind it (the same trap the merchant panel already taught this
    /// codebase). And the exchange itself can be closed mid-sequence, at which point a stale rect
    /// still points at where the panel used to be and the click lands on the world behind it.
    /// </summary>
    private async Task<ExchangePullResult> PullExchangeOrder(int index, int inventoryBefore)
    {
        for (var attempt = 0; attempt < ExchangeCollectAttempts; attempt++)
        {
            if (ExchangeCollectStopping()) return ExchangePullResult.Aborted;

            var ready = await ExchangeReadyToClick();
            if (!ready.Ok)
            {
                StopExchangeRun(ready.Missing);
                return ExchangePullResult.Aborted;
            }

            var rows = CollectableExchangeOrdersIn(FindExchangeOrderList(ready.Panel));

            // The list shrinks as orders are emptied, so an index that was valid when the cycle
            // started may not be now. That is a finished order, not a failure.
            if (index >= rows.Count) return ExchangePullResult.OrderGone;

            var slot = NavigateLowerPriceChildren(rows[index], ExchangeOrderBuySlotIndex);
            if (slot == null) return ExchangePullResult.OrderGone;

            RectangleF slotRect;
            try { slotRect = slot.GetClientRectCache; } catch { continue; }
            if (slotRect.Width <= 0 || slotRect.Height <= 0) continue;

            if (attempt > 0) await NudgeExchangeCursorAway(slotRect);

            if (!await ExchangeCtrlRightClickChecked(slotRect, ready.PanelRect, "an order slot"))
                return ExchangePullResult.Aborted;

            if (await WaitForExchangeCondition(() => CurrentExchangeInventoryCount() > inventoryBefore,
                                               ExchangeCollectMoveTimeoutMs, requirePanel: true))
                return ExchangePullResult.Pulled;
        }

        return ExchangePullResult.NothingArrived;
    }

    /// <summary>
    /// Pulls every filled order into the stash: collect into the inventory, empty the inventory into
    /// the stash, repeat until the orders' Buying slots are empty.
    /// </summary>
    private async Task CollectFilledExchangeOrders()
    {
        var collected = 0;
        var idleCycles = 0;   // consecutive pulls that got nothing into the stash
        var replans = 0;      // consecutive cycles that found the order list had moved under them

        // NB: _exchangeCollectCancel is cleared by the render thread before the run is marked as
        // in-flight, not here. Clearing it here would throw away a right-click made during the brief
        // wait for the starting left-click to release - a window the watcher is already live for.
        TouchExchangeCollectProgress();
        _exchangeCollectPulls = 0;
        _exchangeStuckStacks.Clear();

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
            // Only meaningful once the inventory is actually on screen. If it isn't, there is nothing
            // to read and nothing to clear - the first collect click opens it, and the drain after
            // that pull sweeps up whatever was already sitting there.
            if (IsExchangeInventoryOpen() && CurrentExchangeInventoryCount() > 0)
            {
                _exchangeCollectStatus = "clearing inventory...";
                if ((await DrainExchangeInventoryToStash()).Result == ExchangeDrainResult.Stuck)
                    return; // The drain has already set a status explaining why.
            }

            // No cycle cap. It was a second backstop behind the watchdog and it capped the wrong
            // thing: how many times a HEALTHY run went round, which for a big order is however many
            // inventory-loads the order is worth. What actually has to be bounded is a run that has
            // stopped getting anywhere, and every way that can happen now ends the loop on its own -
            // no collectable orders left, a pull that returns nothing, a drain that can't reach the
            // stash, a panel that closed, the idle watchdog, or the user.
            while (true)
            {
                if (ExchangeCollectStopped()) return;

                var ready = await ExchangeReadyToClick();
                if (!ready.Ok)
                {
                    StopExchangeRun(ready.Missing);
                    return;
                }

                var orders = CollectableExchangeOrders(ready.Panel);
                if (orders.Count == 0)
                {
                    var leftover = CurrentExchangeInventoryCount();
                    _exchangeCollectStatus = collected == 0
                        ? "nothing to collect"
                        : leftover > 0
                            ? $"done - {collected} pull(s) stashed, {leftover} stack(s) left behind" +
                              "\nthe panel - close the exchange to stash them"
                            : $"done - {collected} pull(s) stashed";
                    LogMessage($"ExchangeCollect: finished after {collected} pull(s), {leftover} left over.");
                    return;
                }

                // Every ready order gets a go, not just the first. One order that won't hand its
                // contents over used to end the run outright, leaving every order behind it
                // uncollected; now it is stepped over and only a list where NOTHING moves stops
                // anything.
                var pulled = false;
                var listMoved = false;

                for (var index = 0; index < orders.Count && !pulled; index++)
                {
                    if (ExchangeCollectStopped()) return;

                    _exchangeCollectStatus = $"collecting {ExchangeOrderBuyingLabel(orders[index]) ?? "?"} " +
                                             $"({orders.Count - index} order(s) left)";

                    // Compared against the count BEFORE the click, not against zero. Stacks stranded
                    // under the panel keep the inventory non-empty, so a "> 0" test would pass
                    // without anything new having arrived - and the loop would spin until the cycle
                    // limit.
                    var result = await PullExchangeOrder(index, CurrentExchangeInventoryCount());

                    if (result == ExchangePullResult.Aborted) return; // Status already set.

                    if (result == ExchangePullResult.OrderGone)
                    {
                        // The list this cycle was planned against no longer exists. Re-plan against
                        // the one that does, rather than working through indices into a stale list
                        // and then reporting the resulting nothing as a failure - which is how a run
                        // that had in fact collected everything used to end on an error.
                        listMoved = true;
                        break;
                    }

                    pulled = result == ExchangePullResult.Pulled;

                    if (!pulled)
                        LogMessage($"ExchangeCollect: order {index + 1} of {orders.Count} gave nothing " +
                                   "back; trying the next one.");
                }

                if (listMoved)
                {
                    // Re-planning is the right response, but it is also the one path round this loop
                    // that reaches `continue` without having awaited anything - no click, no wait.
                    // With the cycle cap gone, a panel that reports orders to the outer read and none
                    // to the inner one would spin this thread flat out until the watchdog noticed.
                    // A tick of breathing room costs nothing and makes that impossible; the counter
                    // then turns "the list keeps moving" into a message rather than a silent stall.
                    await Task.Delay(50);

                    if (++replans < ExchangeCollectMaxReplans) continue;

                    _exchangeCollectStatus = "stopped - the order list keeps changing";
                    LogError($"ExchangeCollect: re-read the order list {replans} times in a row and " +
                             "the orders it lists were gone by the time they were clicked. Stopping. " +
                             "Reopen the Currency Exchange and press GET again.");
                    return;
                }

                replans = 0;

                if (!pulled)
                {
                    // Every ready order was tried and none of them gave anything back. Either the
                    // interaction changed, or the free cells are gone because the covered columns
                    // have filled up.
                    var stranded = CurrentExchangeInventoryCount();
                    _exchangeCollectStatus = stranded > 0
                        ? $"stopped after {collected} pull(s) - {stranded} stack(s) still in the bag"
                        : $"nothing arrived in the inventory after {collected} pull(s)";
                    LogError($"ExchangeCollect: ctrl+right-clicked all {orders.Count} ready order(s), " +
                             $"{ExchangeCollectAttempts} attempts each, and nothing reached the " +
                             $"inventory ({stranded} stack(s) in it). Stopping.");
                    return;
                }

                // The pull opens the inventory by itself. If it somehow isn't open now, every
                // subsequent read would say "empty" and the loop would keep pulling into it.
                if (!IsExchangeInventoryOpen())
                {
                    _exchangeCollectStatus = "stopped - the inventory didn't open";
                    LogError("ExchangeCollect: currency was pulled but the inventory panel isn't open, " +
                             "so its contents can't be read. Stopping before pulling any more.");
                    return;
                }

                collected++;
                _exchangeCollectPulls = collected;
                TouchExchangeCollectProgress();

                _exchangeCollectStatus = $"stashing (pull {collected})...";
                var drained = await DrainExchangeInventoryToStash();
                if (drained.Result == ExchangeDrainResult.Stuck)
                    return; // The drain has already set a status explaining why.

                // Whether to go round again is decided on what reached the STASH, not on what the
                // drain called itself. A pass that shifted nothing means every cell the picker can
                // reach is spoken for, so the next pull would only bury more currency in the columns
                // the panel covers - which is the loop that had the run cheerfully collecting into a
                // bag it had already given up on emptying, over and over.
                idleCycles = drained.Stashed > 0 ? 0 : idleCycles + 1;

                if (idleCycles >= ExchangeCollectMaxIdleCycles)
                {
                    var stranded = CurrentExchangeInventoryCount();
                    _exchangeCollectStatus = $"stopped - {stranded} stack(s) stuck in the bag" +
                                             "\nclose the exchange to stash them";
                    LogError($"ExchangeCollect: {idleCycles} pull(s) in a row got nothing into the " +
                             $"stash and the inventory still holds {stranded} stack(s) - they are " +
                             "behind the Currency Exchange panel, which covers the first two columns " +
                             "of the grid. Close the exchange and stash them by hand, then press GET " +
                             "again. Stopping rather than filling the bag further.");
                    return;
                }
            }
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

            _exchangeCollectDeadline = DateTime.MaxValue;
            _exchangeCollectCancel = false;
        }
    }

    /// <summary>Draws the collect button on the Currency Exchange panel, beside Place Order.</summary>
    partial void RenderExchangeCollect()
    {
        try
        {
            // Before anything else, and before the panel lookup that can bail out: while a run is in
            // flight the user's right-click is a stop, and it has to be seen on the frame it happens
            // rather than whenever the run next gets round to looking.
            if (Volatile.Read(ref _exchangeCollectRunning) == 1)
                WatchForExchangeCollectRightClickCancel();
            else
                _exchangeUserRightDownSince = null;

            var panel = FindCurrencyExchangePanel();
            if (panel == null) return;

            // In the empty parchment strip just above the order list, hard against its left edge.
            //
            // It used to sit to the RIGHT of the list, on the assumption the list was a single 486px
            // column. Orders are laid out in TWO columns (972px total), so that spot is now on top
            // of the second column's first Buying slot. The strip above the list stays empty however
            // many orders there are, and it is still hundreds of pixels clear of Place Order - a
            // stray click there places a real currency order.
            var list = FindExchangeOrderList(panel);
            var anchor = list?.GetClientRectCache ?? default;
            if (anchor.Width <= 0)
            {
                // No orders, so no list to measure. Fall back to the same spot relative to the
                // panel, which is where the list would be if there were any.
                var panelRect = panel.GetClientRectCache;
                if (panelRect.Width <= 0) return;
                anchor = new RectangleF(panelRect.X + 18f, panelRect.Y + 342f, 1f, 1f);
            }

            var buttonRect = new RectangleF(anchor.X + ExchangeCollectButtonInset,
                                            anchor.Y - ExchangeCollectButtonAboveList,
                                            ExchangeCollectButtonSize, ExchangeCollectButtonSize);

            // While a run is in flight the same button is the way OUT of it. A feature that takes
            // the mouse away from you has to give it back on demand, and "hold right mouse" was
            // never that: the run holds right mouse itself, and only looked between orders anyway.
            var running = Volatile.Read(ref _exchangeCollectRunning) == 1;

            // Re-using the list the caller already found, rather than walking the panel a second
            // time - this runs every frame. Skipped entirely mid-run: the count is meaningless while
            // orders are being drained, and the status line says more.
            var ready = running ? 0 : CollectableExchangeOrdersIn(list).Count;

            // Red STOP while running, green while there is something to collect, grey when there
            // isn't - so the panel says at a glance whether an order has filled, without opening
            // anything.
            Graphics.DrawBox(buttonRect, running
                ? new SharpDX.Color(150, 40, 40, 230)
                : ready > 0
                    ? new SharpDX.Color(40, 140, 60, 220)
                    : new SharpDX.Color(70, 70, 70, 160));
            Graphics.DrawFrame(buttonRect, SharpDX.Color.Black, 1);
            Graphics.DrawText(running ? "STOP" : ready > 0 ? $"GET{ready}" : "GET",
                              new Vector2(buttonRect.X + 3, buttonRect.Y + 11),
                              running || ready > 0 ? SharpDX.Color.White : SharpDX.Color.Gray);

            var status = _exchangeCollectStatus;
            if (!string.IsNullOrEmpty(status))
                Graphics.DrawText(status, new Vector2(buttonRect.X, buttonRect.Y + buttonRect.Height + 4),
                                  SharpDX.Color.White);

            // A feature that takes the mouse has to say how to take it back, and it has to say it
            // while it has the mouse - which is the one moment the user can't go looking.
            if (running)
                Graphics.DrawText("right-click to stop",
                                  new Vector2(buttonRect.X + buttonRect.Width + 6, buttonRect.Y + 11),
                                  SharpDX.Color.Gray);

            if (!IsExchangeCollectButtonPressed(buttonRect)) return;

            if (running)
            {
                _exchangeCollectCancel = true;
                _exchangeCollectStatus = "stopping...";
                return;
            }

            if (ready == 0)
            {
                _exchangeCollectStatus = "nothing to collect";
                return;
            }

            // Cleared before the run is marked in-flight, because the watcher starts looking the
            // instant it is - and anything it sets from that moment on has to survive.
            _exchangeCollectCancel = false;
            _exchangeUserRightDownSince = null;

            // One run at a time, or a second click starts a pass that fights the first for the mouse.
            if (Interlocked.Exchange(ref _exchangeCollectRunning, 1) == 1) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    // Waiting out the click that started this, so the run's first synthetic click
                    // isn't merged into it. Bounded: an unbounded wait here meant a left button that
                    // reads as stuck down - another feature's click that never released, a lost
                    // WM_LBUTTONUP - wedged _exchangeCollectRunning at 1 and the button dead until
                    // the HUD restarted.
                    var waited = 0;
                    while (Control.MouseButtons == MouseButtons.Left && waited < 2000)
                    {
                        await Task.Delay(10);
                        waited += 10;
                    }

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

    /// <summary>
    /// A press that started on the button, as opposed to a button that happened to slide under an
    /// already-held cursor.
    ///
    /// The previous state used to be keyed by the button's RECTANGLE, and the button is anchored to
    /// the order list - which moves and resizes as orders fill and disappear. A rect that changed
    /// between frames was a fresh key with no history, so the press was dropped. That is survivable
    /// for GET (click again) and not for STOP, which is needed exactly when the run is churning the
    /// list. One button, one field.
    /// </summary>
    private bool IsExchangeCollectButtonPressed(RectangleF buttonRect)
    {
        try
        {
            var prevState = _exchangeCollectButtonState;
            var cursorPos = Utility.Mouse.GetCursorPosition();
            var windowPos = GameController.Window.GetWindowRectangleTimeCache.TopLeft;
            var x = cursorPos.X - windowPos.X;
            var y = cursorPos.Y - windowPos.Y;

            var hovered = x >= buttonRect.X && x <= buttonRect.X + buttonRect.Width &&
                          y >= buttonRect.Y && y <= buttonRect.Y + buttonRect.Height;

            if (!hovered)
            {
                _exchangeCollectButtonState = null;
                return false;
            }

            var pressed = Control.MouseButtons == MouseButtons.Left;
            _exchangeCollectButtonState = pressed;
            return pressed && prevState == false;
        }
        catch
        {
            return false;
        }
    }
}
