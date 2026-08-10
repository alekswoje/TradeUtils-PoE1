using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using TradeUtils.Models;

// Both the trade API models and the game's component set define a "Mods". This file needs the
// game one; the API one is reached through Item.
using ModsComponent = ExileCore.PoEMemory.Components.Mods;

namespace TradeUtils;

/// <summary>
/// Confirms that the item sitting in the seller's stash slot is the exact listing that was fetched,
/// before anything gets clicked.
///
/// This matters more than it sounds. A seller can have two copies of the same unique in the same
/// tab where one is identified and one isn't, and for something like The Light of Meaning that is
/// the difference between a gamble and a known quantity. The listing only carries a stash
/// coordinate, so if the item moved, sold, or was replaced between the fetch and the teleport, the
/// coordinate still points at *something* — just not the thing that was paid for.
///
/// Everything here reads game memory rather than the clipboard. Ctrl+C parsing was the original
/// plan (and is still present, unused, in the teleport code) but it fights the plugin for the
/// clipboard, needs the item hovered first, and fails silently when the tooltip is slow.
/// </summary>
public partial class TradeUtils
{
    /// <summary>Outcome of matching a fetched listing against the item actually in the slot.</summary>
    private sealed class SlotVerification
    {
        public bool Matches;
        /// <summary>Why it was rejected, phrased for the log. Null when it matched.</summary>
        public string Reason;
        /// <summary>What was actually compared, so a pass isn't just a silent "ok".</summary>
        public string Checked;
        /// <summary>Where the item is and what it is. Null when the slot couldn't be resolved.</summary>
        public PurchaseSlot Slot;
    }

    /// <summary>What is at a stash coordinate in the seller's tab, and where to click it.</summary>
    private sealed class PurchaseSlot
    {
        /// <summary>The item in the slot, from the server-side inventory. Null on failure.</summary>
        public Entity Item;

        /// <summary>
        /// Address of the item entity, and of the inventory it came from.
        ///
        /// Useful as a fast "nothing moved" check, but NOT proof of identity: the panel re-seats
        /// every entity whenever any listing in the tab changes, so the same physical item comes
        /// back at a new address. Anything concluding "this is a different item" from these alone
        /// will be wrong regularly.
        /// </summary>
        public long ItemAddress;
        public long InventoryAddress;

        /// <summary>
        /// Name of the seller's tab this slot was read from, when the client exposes it.
        ///
        /// This is the check that makes buying several items from one seller safe: each item is
        /// reached by sending another hideout token, and the tab switch is not instant. Comparing
        /// against the listing's own stash name catches the case where the client is still showing
        /// the previous tab and the coordinate now points at something else entirely.
        /// </summary>
        public string TabName;

        /// <summary>Screen coordinates to click, already offset by the game window.</summary>
        public int ClickX;
        public int ClickY;
        public bool HasClickPoint;

        /// <summary>Item footprint in cells, from the server inventory.</summary>
        public int CellsWide;
        public int CellsHigh;

        /// <summary>How the click point was derived — logged so a misclick is traceable.</summary>
        public string ClickSource;

        /// <summary>Null when the slot resolved. Otherwise says precisely what went wrong.</summary>
        public string Failure;

        public bool Ok => Failure == null && Item != null;
    }

    /// <summary>
    /// Resolves what sits at a stash coordinate in the seller's open tab, and where on screen to
    /// click it.
    ///
    /// The authority is <c>VisibleStash.ServerInventory</c>, which has a direct (x,y) indexer and
    /// reports the tab's real <c>Columns</c>/<c>Rows</c>. The UI element list is used only to get a
    /// pixel-accurate rectangle, and is matched by entity address rather than by coordinate:
    /// <c>NormalInventoryItem.InventPosX/Y</c> read as 0,0 for every item in the merchant panel, so
    /// matching on them finds nothing and makes a tab full of items look empty.
    ///
    /// Every failure path returns its own message. Collapsing them into one "nothing is in that
    /// slot" is what made a tab-read problem look identical to a sold listing.
    /// </summary>
    /// <param name="preferInventoryAddress">
    /// When non-zero, only this inventory is consulted. Re-checking a slot must look at the tab it
    /// was originally found in — scanning every candidate again can land on a different tab that
    /// happens to have something at the same coordinate, and report it as the item having changed.
    /// </param>
    private PurchaseSlot ResolvePurchaseSlot(int x, int y, long preferInventoryAddress = 0)
    {
        var result = new PurchaseSlot();

        try
        {
            var purchaseWindow = GameController?.IngameState?.IngameUi?.PurchaseWindowHideout;
            if (purchaseWindow == null || !purchaseWindow.IsVisible)
            {
                result.Failure = "the trade window isn't open";
                return result;
            }

            var container = purchaseWindow.TabContainer;
            if (container == null)
            {
                result.Failure = "the trade window has no tab container yet";
                return result;
            }

            // Try the open tab first, then every other inventory the container exposes. Which
            // object actually backs this panel isn't something to bet on blind — but an item that
            // is drawn on screen is by definition in the tab the seller has open, so the candidate
            // that yields a real screen rectangle is the right one whatever it is called.
            var candidates = CollectSlotCandidates(container);

            if (preferInventoryAddress != 0)
                candidates = candidates.Where(c => c.Address == preferInventoryAddress).ToList();

            if (candidates.Count == 0)
            {
                result.Failure = preferInventoryAddress != 0
                    ? "the seller's tab is no longer readable"
                    : "could not read any of the seller's tabs";
                return result;
            }

            PurchaseSlot undrawn = null;

            foreach (var candidate in candidates)
            {
                var server = candidate.ServerInventory;
                if (server == null) continue;

                var slotItem = server[x, y];
                var entity = slotItem?.Item;
                if (entity == null) continue;

                var found = new PurchaseSlot
                {
                    Item = entity,
                    ItemAddress = SafeAddress(entity),
                    InventoryAddress = candidate.Address,
                    TabName = ReadTabName(container, candidate),
                    CellsWide = Math.Max(slotItem.SizeX, 1),
                    CellsHigh = Math.Max(slotItem.SizeY, 1)
                };

                ResolveClickPoint(found, candidate, purchaseWindow, entity, x, y, server.Columns, server.Rows);

                // A drawn rectangle proves this is the visible tab. Take it and stop looking.
                if (found.HasClickPoint && found.ClickSource == "item element")
                    return found;

                undrawn = undrawn ?? found;
            }

            // Nothing was drawn. Either the client hasn't laid the tab out yet, or the item is in a
            // tab that isn't showing — the grid fallback in ResolveClickPoint already aimed at the
            // visible panel, so this is still clickable if the coordinates line up.
            if (undrawn != null) return undrawn;

            var visibleServer = container.VisibleStash?.ServerInventory;
            result.Failure = visibleServer == null
                ? "the seller's open tab hasn't loaded its contents yet"
                : DescribeMissingSlot(visibleServer, container, x, y, visibleServer.Columns, visibleServer.Rows);

            return result;
        }
        catch (Exception ex)
        {
            result.Failure = $"could not read the seller's tab ({ex.Message})";
            return result;
        }
    }

    /// <summary>Name of the seller's currently open tab, or null if it can't be read.</summary>
    private string CurrentPurchaseTabName()
    {
        try
        {
            var purchaseWindow = GameController?.IngameState?.IngameUi?.PurchaseWindowHideout;
            if (purchaseWindow == null || !purchaseWindow.IsVisible) return null;

            var container = purchaseWindow.TabContainer;
            return ReadTabName(container, container?.VisibleStash);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the tab this listing lives in is the one already on screen.
    ///
    /// When it is, the item can simply be verified and clicked — the hideout token exists to
    /// teleport and to page the trade window onto the right tab, and neither is needed for an item
    /// that is already displayed. Skipping it saves an API call against the teleport quota.
    ///
    /// Deliberately false when either name is unreadable: an unknown answer must send the token,
    /// because acting on the assumption that the right tab is open is exactly the mistake worth
    /// avoiding.
    /// </summary>
    private bool IsListingTabAlreadyOpen(ResultItem listing)
    {
        string wanted = listing?.Listing?.Stash?.Name;
        if (string.IsNullOrWhiteSpace(wanted)) return false;

        string open = CurrentPurchaseTabName();
        if (string.IsNullOrWhiteSpace(open)) return false;

        return string.Equals(wanted.Trim(), open, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Name of the tab an inventory belongs to, or null if the client doesn't say.</summary>
    private string ReadTabName(
        ExileCore.PoEMemory.Elements.StashTabContainer container,
        ExileCore.PoEMemory.MemoryObjects.Inventory inventory)
    {
        try
        {
            var tabs = container?.Inventories;
            if (tabs == null || inventory == null) return null;

            foreach (var tab in tabs)
            {
                if (tab?.Inventory == null) continue;
                if (tab.Inventory.Address != inventory.Address) continue;

                var name = tab.TabName;
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
        }
        catch
        {
            // Advisory only — a missing name just means this check is skipped.
        }

        return null;
    }

    private static long SafeAddress(Entity entity)
    {
        try
        {
            return entity?.Address ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Waits until the client is actually showing the listing, then returns the verification.
    ///
    /// Replaces guessing with a fixed delay after sending a hideout token. The tab switch takes an
    /// unknown amount of time, and a delay that is too short means verifying — and clicking — while
    /// the previous tab is still on screen. Polling turns that into a positive confirmation: it
    /// proceeds the moment the right item is in the right tab, and gives up if it never arrives.
    /// </summary>
    private async Task<SlotVerification> WaitForListingInSlotAsync(
        ResultItem listing,
        int x,
        int y,
        int timeoutMs,
        System.Threading.CancellationToken ct)
    {
        var deadline = DateTime.Now.AddMilliseconds(Math.Max(timeoutMs, 250));
        SlotVerification latest;

        while (true)
        {
            latest = VerifyListingInSlot(listing, x, y);

            if (latest.Matches) return latest;
            if (DateTime.Now >= deadline || !_bulkBuyInProgress || ct.IsCancellationRequested) return latest;

            await System.Threading.Tasks.Task.Delay(150, ct);
        }
    }

    /// <summary>
    /// Re-checks that a slot still holds the listing, after it was already approved once.
    ///
    /// The naive version of this compared entity pointers and skipped on any difference, which
    /// produced "the item in that slot changed" for items that hadn't moved at all. The panel
    /// re-seats every entity in a tab whenever any listing in it changes — someone else buying an
    /// unrelated item from the same seller is enough — so the same physical item reappears at a new
    /// address. Hovering it can be enough to catch the list mid-rebuild.
    ///
    /// So the address is only a fast path. When it differs, the question is re-asked properly:
    /// does the item now in that slot still match the listing on every hard field? If it does, it
    /// is the item we approved, whatever the pointer says.
    /// </summary>
    private SlotVerification ReconfirmSlot(ResultItem listing, int x, int y, PurchaseSlot approved)
    {
        var current = ResolvePurchaseSlot(x, y, approved?.InventoryAddress ?? 0);

        if (current.Ok && approved != null && current.ItemAddress != 0 && current.ItemAddress == approved.ItemAddress)
        {
            // Same entity, still there. Nothing more to prove.
            return new SlotVerification { Matches = true, Slot = current, Checked = "unchanged" };
        }

        if (!current.Ok)
            return new SlotVerification { Matches = false, Reason = current.Failure, Slot = current };

        // Different address. Re-run the full comparison rather than assuming the worst.
        var reverified = VerifyListingInSlot(listing, x, y, approved?.InventoryAddress ?? 0);

        if (reverified.Matches)
            LogDebug("BulkBuy: the tab re-seated its items; the slot still holds the listed item.");

        return reverified;
    }

    /// <summary>
    /// Dumps what the client is actually reporting for the seller's panel.
    ///
    /// Called when a slot can't be resolved, so a failure produces the evidence needed to fix it
    /// rather than another round of guessing. In particular it prints the server-side coordinates
    /// next to the UI element's own <c>InventPosX/Y</c>, which is where these two disagree.
    /// </summary>
    private void LogPurchaseWindowDiagnostics(int wantX, int wantY)
    {
        try
        {
            var purchaseWindow = GameController?.IngameState?.IngameUi?.PurchaseWindowHideout;
            LogMessage($"── BulkBuy diagnostics for slot ({wantX},{wantY}) ──");
            LogMessage($"   purchase window: {(purchaseWindow == null ? "null" : purchaseWindow.IsVisible ? "visible" : "hidden")}");

            var container = purchaseWindow?.TabContainer;
            if (container == null)
            {
                LogMessage("   tab container: null");
                return;
            }

            LogMessage($"   TotalStashes={container.TotalStashes}, VisibleStashIndex={container.VisibleStashIndex}, " +
                       $"Inventories={SafeCount(() => container.Inventories?.Count)}, " +
                       $"AllInventories={SafeCount(() => container.AllInventories?.Count)}");

            var visible = container.VisibleStash;
            if (visible == null)
            {
                LogMessage("   VisibleStash: null");
            }
            else
            {
                var server = visible.ServerInventory;
                LogMessage($"   VisibleStash: addr=0x{visible.Address:X}, " +
                           $"grid={server?.Columns ?? -1}x{server?.Rows ?? -1}, " +
                           $"serverItems={SafeCount(() => server?.InventorySlotItems?.Count(s => s?.Item != null))}, " +
                           $"elements={SafeCount(() => visible.VisibleInventoryItems?.Count)}");

                DumpSlotSample(visible);
            }

            var tabs = container.Inventories;
            if (tabs != null)
            {
                for (int i = 0; i < tabs.Count && i < 12; i++)
                {
                    var inventory = tabs[i]?.Inventory;
                    var server = inventory?.ServerInventory;
                    LogMessage($"   tab[{i}] '{tabs[i]?.TabName}': addr=0x{inventory?.Address ?? 0:X}, " +
                               $"grid={server?.Columns ?? -1}x{server?.Rows ?? -1}, " +
                               $"items={SafeCount(() => server?.InventorySlotItems?.Count(s => s?.Item != null))}, " +
                               $"hasWanted={(server?[wantX, wantY]?.Item != null)}");
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: diagnostics failed — {ex.Message}");
        }
    }

    /// <summary>
    /// Prints the first few occupied slots with both coordinate sources side by side. If the
    /// element column reads 0,0 for everything, the UI list is the unreliable one.
    /// </summary>
    private void DumpSlotSample(ExileCore.PoEMemory.MemoryObjects.Inventory inventory)
    {
        try
        {
            var slots = inventory.ServerInventory?.InventorySlotItems;
            if (slots == null) return;

            int shown = 0;
            foreach (var slot in slots)
            {
                if (slot?.Item == null) continue;
                if (shown++ >= 5) break;

                string element = "no element";
                var match = inventory.VisibleInventoryItems?
                    .FirstOrDefault(e => e?.Item != null && e.Item.Address == slot.Item.Address);

                if (match != null)
                {
                    var rect = match.GetClientRectCache;
                    element = $"element InventPos=({match.InventPosX},{match.InventPosY}) " +
                              $"rect=({rect.X:F0},{rect.Y:F0} {rect.Width:F0}x{rect.Height:F0})";
                }

                LogMessage($"     server ({slot.PosX},{slot.PosY}) {slot.SizeX}x{slot.SizeY} " +
                           $"'{slot.Item.Path?.Split('/').LastOrDefault()}' — {element}");
            }
        }
        catch (Exception ex)
        {
            LogDebug($"BulkBuy: slot sample failed — {ex.Message}");
        }
    }

    private static string SafeCount(Func<int?> read)
    {
        try
        {
            return read()?.ToString() ?? "null";
        }
        catch
        {
            return "err";
        }
    }

    /// <summary>
    /// Every inventory that might back the seller's panel, open tab first, de-duplicated by
    /// address. ExileCore exposes the same tabs three ways and which one is populated has moved
    /// between patches, so all of them are tried rather than one being assumed.
    /// </summary>
    private List<ExileCore.PoEMemory.MemoryObjects.Inventory> CollectSlotCandidates(
        ExileCore.PoEMemory.Elements.StashTabContainer container)
    {
        var candidates = new List<ExileCore.PoEMemory.MemoryObjects.Inventory>();
        var seen = new HashSet<long>();

        void Add(ExileCore.PoEMemory.MemoryObjects.Inventory inventory)
        {
            if (inventory == null) return;
            if (!seen.Add(inventory.Address)) return;
            candidates.Add(inventory);
        }

        try
        {
            Add(container.VisibleStash);
        }
        catch (Exception ex)
        {
            LogDebug($"BulkBuy: VisibleStash unreadable — {ex.Message}");
        }

        try
        {
            var tabs = container.Inventories;
            if (tabs != null)
                foreach (var tab in tabs) Add(tab?.Inventory);
        }
        catch (Exception ex)
        {
            LogDebug($"BulkBuy: tab inventories unreadable — {ex.Message}");
        }

        try
        {
            var all = container.AllInventories;
            if (all != null)
                foreach (var inventory in all) Add(inventory);
        }
        catch (Exception ex)
        {
            LogDebug($"BulkBuy: AllInventories unreadable — {ex.Message}");
        }

        return candidates;
    }

    /// <summary>
    /// Works out where to click. Prefers the item's own drawn rectangle; falls back to grid
    /// arithmetic using the tab's real column/row count.
    ///
    /// The fallback deliberately does not assume 12x12 the way the rest of the plugin does. A quad
    /// tab is 24x24, and dividing its panel by 12 aims at roughly double-scale coordinates — which
    /// puts the click on the wrong item rather than merely missing.
    /// </summary>
    private void ResolveClickPoint(
        PurchaseSlot slot,
        ExileCore.PoEMemory.MemoryObjects.Inventory candidate,
        ExileCore.PoEMemory.Elements.PurchaseWindow purchaseWindow,
        Entity entity,
        int x,
        int y,
        int columns,
        int rows)
    {
        var window = GameController.Window.GetWindowRectangle();
        var panelRect = purchaseWindow.GetClientRectCache;

        // 1. The element the client actually drew for this entity.
        try
        {
            var elements = candidate.VisibleInventoryItems;
            if (elements != null)
            {
                foreach (var element in elements)
                {
                    if (element?.Item == null) continue;
                    if (element.Item.Address != entity.Address) continue;

                    var rect = element.GetClientRectCache;
                    if (rect.Width <= 0 || rect.Height <= 0) break;

                    float centreX = rect.X + rect.Width / 2f;
                    float centreY = rect.Y + rect.Height / 2f;

                    // An element the client hasn't laid out reports at the window origin with a
                    // plausible size, so a size check alone waves it through and the click lands in
                    // the corner of the screen. Require it to actually sit inside the trade window.
                    if (!IsInside(panelRect, centreX, centreY)) break;

                    slot.ClickX = (int)(window.X + centreX);
                    slot.ClickY = (int)(window.Y + centreY);
                    slot.HasClickPoint = true;
                    slot.ClickSource = "item element";
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            LogDebug($"BulkBuy: could not read the item's drawn rect — {ex.Message}");
        }

        // 2. Grid arithmetic over the visible stash panel, using the tab's true dimensions.
        try
        {
            if (columns <= 0 || rows <= 0) return;

            var gridRect = purchaseWindow.TabContainer?.StashInventoryPanel?.GetClientRectCache ?? default;
            if (gridRect.Width <= 0 || gridRect.Height <= 0) return;
            if (!IsInside(panelRect, gridRect.X + 1, gridRect.Y + 1)) return;

            float cellWidth = gridRect.Width / columns;
            float cellHeight = gridRect.Height / rows;

            // Centre of the item's whole footprint, so wide items aren't clicked on their edge.
            float centreX = gridRect.X + (x + slot.CellsWide / 2f) * cellWidth;
            float centreY = gridRect.Y + (y + slot.CellsHigh / 2f) * cellHeight;

            if (!IsInside(gridRect, centreX, centreY)) return;

            slot.ClickX = (int)(window.X + centreX);
            slot.ClickY = (int)(window.Y + centreY);
            slot.HasClickPoint = true;
            slot.ClickSource = $"{columns}x{rows} grid";
        }
        catch (Exception ex)
        {
            LogDebug($"BulkBuy: could not compute a grid click point — {ex.Message}");
        }
    }

    /// <summary>
    /// Whether a point falls inside a rectangle, used as a whereabouts check on click targets.
    /// A positive test on both axes is what distinguishes a laid-out element from one reporting at
    /// the window origin.
    /// </summary>
    private static bool IsInside(SharpDX.RectangleF bounds, float pointX, float pointY)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;

        return pointX >= bounds.X && pointX <= bounds.X + bounds.Width &&
               pointY >= bounds.Y && pointY <= bounds.Y + bounds.Height;
    }

    /// <summary>
    /// Builds a message for an empty slot that says enough to tell the three causes apart: the
    /// listing sold, the wrong tab is open, or the coordinate didn't survive the round trip.
    /// </summary>
    private string DescribeMissingSlot(
        ServerInventory server,
        ExileCore.PoEMemory.Elements.StashTabContainer container,
        int x,
        int y,
        int columns,
        int rows)
    {
        int occupied = 0;
        try
        {
            occupied = server.InventorySlotItems?.Count(s => s?.Item != null) ?? 0;
        }
        catch
        {
            // Counting is best-effort; the message is still useful without it.
        }

        if (columns > 0 && rows > 0 && (x >= columns || y >= rows))
            return $"slot ({x},{y}) is outside the seller's open tab, which is only {columns}x{rows} — " +
                   "the wrong tab is open";

        if (occupied == 0)
            return $"the seller's open tab reads as empty ({columns}x{rows}), so the listing's tab isn't the one showing";

        // The tab has items and the coordinate is in range, so this really does look like a sale.
        string other = FindCoordinateInOtherTabs(container, x, y);
        if (other != null)
            return $"slot ({x},{y}) is empty in the open tab, but an item sits there in {other} — the wrong tab is open";

        return $"slot ({x},{y}) is empty — the listing sold or was moved ({occupied} other item(s) in the tab)";
    }

    /// <summary>
    /// Checks whether some other loaded tab has an item at the coordinate. Turns "it sold" into
    /// "the tab switch didn't happen", which are fixed in completely different places.
    /// </summary>
    private string FindCoordinateInOtherTabs(
        ExileCore.PoEMemory.Elements.StashTabContainer container,
        int x,
        int y)
    {
        try
        {
            var tabs = container?.Inventories;
            if (tabs == null) return null;

            // Memory objects get rebuilt, so identity is the address, not the reference.
            long visibleAddress = container.VisibleStash?.Address ?? 0;

            for (int i = 0; i < tabs.Count; i++)
            {
                var inventory = tabs[i]?.Inventory;
                if (inventory == null || inventory.Address == visibleAddress) continue;

                var server = inventory.ServerInventory;
                if (server?[x, y]?.Item == null) continue;

                string name = tabs[i].TabName;
                return string.IsNullOrWhiteSpace(name) ? $"tab index {i}" : $"the '{name}' tab";
            }
        }
        catch
        {
            // Purely diagnostic.
        }

        return null;
    }

    /// <summary>
    /// Compares the fetched listing against whatever is actually in its slot.
    ///
    /// Fields fall into two groups. The ones that change what the item *is* — base type, identified,
    /// rarity, corruption, size, item level, sockets, stack size — are hard checks and any mismatch
    /// rejects the purchase. The unique name is compared only as a note, because memory reports it
    /// unreliably on unidentified items and rejecting on it would block exactly the case this
    /// feature exists to handle.
    ///
    /// A field that cannot be read on the memory side is not treated as a pass. Base type and
    /// identified state are required; if either is unreadable the item is skipped, because "I could
    /// not check" and "it matched" must not look the same.
    /// </summary>
    private SlotVerification VerifyListingInSlot(ResultItem listing, int x, int y, long preferInventoryAddress = 0)
    {
        var result = new SlotVerification();

        var slot = ResolvePurchaseSlot(x, y, preferInventoryAddress);
        result.Slot = slot;

        if (!slot.Ok)
        {
            result.Reason = slot.Failure;
            return result;
        }

        var expected = listing?.Item;
        if (expected == null)
        {
            result.Reason = "the fetched listing carried no item data";
            return result;
        }

        // ---- right tab? ------------------------------------------------------------------------
        // Checked before anything about the item itself, because being on the wrong tab makes every
        // other check meaningless — the coordinate is pointing at some other item.
        string expectedTab = listing.Listing?.Stash?.Name;
        if (!string.IsNullOrWhiteSpace(expectedTab) && !string.IsNullOrWhiteSpace(slot.TabName) &&
            !string.Equals(expectedTab.Trim(), slot.TabName, StringComparison.OrdinalIgnoreCase))
        {
            result.Reason = $"the open tab is '{slot.TabName}' but this listing is in '{expectedTab.Trim()}'";
            return result;
        }

        var entity = slot.Item;

        var mods = TryGetComponent<ModsComponent>(entity);
        var baseComponent = TryGetComponent<Base>(entity);

        var notes = new List<string>();

        // ---- base type (required) -------------------------------------------------------------
        string actualBase = ReadBaseName(entity, baseComponent);
        string expectedBase = FirstNonEmpty(expected.BaseType, expected.TypeLine);

        if (string.IsNullOrWhiteSpace(actualBase))
        {
            result.Reason = "could not read the base type of the item in that slot";
            return result;
        }

        if (string.IsNullOrWhiteSpace(expectedBase))
        {
            result.Reason = "the listing carried no base type to check against";
            return result;
        }

        if (!BaseNamesMatch(actualBase, expectedBase))
        {
            result.Reason = $"base type is '{actualBase}' but the listing was '{expectedBase}'";
            return result;
        }

        notes.Add($"base '{actualBase}'");

        // ---- identified (required) ------------------------------------------------------------
        // The headline check. An identified and an unidentified copy of the same unique are
        // completely different purchases and sit side by side in search results.
        if (mods == null)
        {
            result.Reason = "could not read whether the item in that slot is identified";
            return result;
        }

        bool actualIdentified = mods.Identified;
        if (actualIdentified != expected.Identified)
        {
            result.Reason = $"the listing was {Ident(expected.Identified)} but the item in the slot is {Ident(actualIdentified)}";
            return result;
        }

        notes.Add(Ident(actualIdentified));

        // ---- rarity ---------------------------------------------------------------------------
        var expectedRarity = RarityFromFrameType(expected.FrameType);
        if (expectedRarity.HasValue)
        {
            var actualRarity = mods.ItemRarity;
            if (actualRarity != expectedRarity.Value)
            {
                result.Reason = $"rarity is {actualRarity} but the listing was {expectedRarity.Value}";
                return result;
            }

            notes.Add(actualRarity.ToString().ToLowerInvariant());
        }

        // ---- corruption -----------------------------------------------------------------------
        if (baseComponent != null)
        {
            bool actualCorrupted = baseComponent.isCorrupted;
            if (actualCorrupted != expected.Corrupted)
            {
                result.Reason = $"the listing was {(expected.Corrupted ? "corrupted" : "uncorrupted")} " +
                                $"but the item in the slot is {(actualCorrupted ? "corrupted" : "uncorrupted")}";
                return result;
            }

            if (actualCorrupted) notes.Add("corrupted");
        }

        // ---- footprint ------------------------------------------------------------------------
        // Cheap and completely unambiguous: a 1x1 jewel is not a 2x4 bow.
        if (expected.W > 0 && expected.H > 0 && slot.CellsWide > 0 && slot.CellsHigh > 0)
        {
            if (slot.CellsWide != expected.W || slot.CellsHigh != expected.H)
            {
                result.Reason = $"the item in the slot is {slot.CellsWide}x{slot.CellsHigh} " +
                                $"but the listing was {expected.W}x{expected.H}";
                return result;
            }
        }

        // ---- item level -----------------------------------------------------------------------
        if (expected.Ilvl > 0 && mods.ItemLevel > 0 && mods.ItemLevel != expected.Ilvl)
        {
            result.Reason = $"item level is {mods.ItemLevel} but the listing was ilvl {expected.Ilvl}";
            return result;
        }

        if (expected.Ilvl > 0) notes.Add($"ilvl {expected.Ilvl}");

        // ---- sockets --------------------------------------------------------------------------
        if (expected.Sockets != null && expected.Sockets.Count > 0)
        {
            var sockets = TryGetComponent<Sockets>(entity);
            if (sockets == null)
            {
                result.Reason = $"the listing had {expected.Sockets.Count} socket(s) but the item in the slot has none";
                return result;
            }

            if (sockets.NumberOfSockets != expected.Sockets.Count)
            {
                result.Reason = $"the item in the slot has {sockets.NumberOfSockets} socket(s) " +
                                $"but the listing had {expected.Sockets.Count}";
                return result;
            }

            notes.Add($"{sockets.NumberOfSockets} sockets");
        }

        // ---- stack size -----------------------------------------------------------------------
        // Matters for currency: a listing for 20 chaos that is now a stack of 3 is not the same buy.
        int expectedStack = ReadListedStackSize(expected);
        if (expectedStack > 0)
        {
            var stack = TryGetComponent<Stack>(entity);
            int actualStack = stack?.Size ?? 0;
            if (actualStack > 0 && actualStack != expectedStack)
            {
                result.Reason = $"the stack is {actualStack} but the listing was {expectedStack}";
                return result;
            }

            if (actualStack > 0) notes.Add($"stack {actualStack}");
        }

        // ---- unique name (advisory only) ------------------------------------------------------
        // Not a rejection: memory reports UniqueName inconsistently for unidentified uniques, and
        // that is precisely the case this whole check exists to protect.
        if (!string.IsNullOrWhiteSpace(expected.Name))
        {
            string actualName = mods.UniqueName;
            if (!string.IsNullOrWhiteSpace(actualName) && !BaseNamesMatch(actualName, expected.Name))
                notes.Add($"note: memory calls it '{actualName}'");
        }

        result.Matches = true;
        result.Checked = string.Join(", ", notes);
        return result;
    }

    /// <summary>
    /// Reads the item's base name, preferring the game's own base-item table over the Base
    /// component's display name (which picks up affixes on magic items).
    /// </summary>
    private string ReadBaseName(Entity entity, Base baseComponent)
    {
        try
        {
            string metadata = FirstNonEmpty(entity.Metadata, entity.Path);
            if (!string.IsNullOrWhiteSpace(metadata))
            {
                var baseType = GameController?.Files?.BaseItemTypes?.Translate(metadata);
                if (!string.IsNullOrWhiteSpace(baseType?.BaseName))
                    return baseType.BaseName;
            }
        }
        catch (Exception ex)
        {
            LogDebug($"BulkBuy: base-item lookup failed — {ex.Message}");
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(baseComponent?.Name))
                return baseComponent.Name;
        }
        catch
        {
            // Base.Name reads through memory and can throw mid-rebuild.
        }

        return null;
    }

    /// <summary>
    /// The trade API reports a stack as a "Stack Size" property formatted "12/20". Returns 0 when
    /// the item isn't stackable or the value can't be read.
    /// </summary>
    private static int ReadListedStackSize(Item item)
    {
        var property = item.Properties?.FirstOrDefault(p =>
            string.Equals(p?.Name, "Stack Size", StringComparison.OrdinalIgnoreCase));

        var raw = property?.Values?.FirstOrDefault()?.FirstOrDefault()?.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return 0;

        var slash = raw.IndexOf('/');
        if (slash > 0) raw = raw.Substring(0, slash);

        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0;
    }

    /// <summary>
    /// Maps the trade API's frameType onto the client's rarity enum, and returns null for frames
    /// with no clean equivalent (divination cards and the like) so they simply aren't checked
    /// instead of being rejected wholesale.
    /// </summary>
    private static ItemRarity? RarityFromFrameType(int frameType)
    {
        switch (frameType)
        {
            case 0: return ItemRarity.Normal;
            case 1: return ItemRarity.Magic;
            case 2: return ItemRarity.Rare;
            case 3: return ItemRarity.Unique;
            case 4: return ItemRarity.Gem;
            case 5: return ItemRarity.Currency;
            case 7: return ItemRarity.Quest;
            // 9/10 are foil and supporter-foil uniques; the client still calls them unique.
            case 9:
            case 10: return ItemRarity.Unique;
            default: return null;
        }
    }

    private T TryGetComponent<T>(Entity entity) where T : Component, new()
    {
        try
        {
            return entity.GetComponent<T>();
        }
        catch (Exception ex)
        {
            LogDebug($"BulkBuy: could not read {typeof(T).Name} off the item — {ex.Message}");
            return null;
        }
    }

    private static readonly Regex NameDecorationRegex = new Regex(@"<<[^>]*>>", RegexOptions.Compiled);

    /// <summary>
    /// Compares item names tolerantly enough to survive the client and the API disagreeing about
    /// decoration, but not so tolerantly that different items collide.
    /// </summary>
    private static bool BaseNamesMatch(string a, string b)
    {
        return string.Equals(NormalizeItemName(a), NormalizeItemName(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeItemName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        // The API prefixes some names with markup like "<<set:MS>><<set:M>><<set:S>>".
        name = NameDecorationRegex.Replace(name, "");

        // Quality normal items read as "Superior <base>" in memory but plain in the API.
        if (name.StartsWith("Superior ", StringComparison.OrdinalIgnoreCase))
            name = name.Substring("Superior ".Length);

        // The client renders some names with a non-breaking space where the API uses a plain one.
        return name.Replace('\u00A0', ' ').Trim();
    }

    private static string Ident(bool identified) => identified ? "identified" : "unidentified";

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();

        return null;
    }
}
