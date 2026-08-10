using System;
using System.Numerics;
using System.Linq;
using ImGuiNET;
using ExileCore.Shared.Nodes;

namespace TradeUtils;

public partial class TradeUtils
{
    /// <summary>
    /// Applies timing preset values based on preset index (0=Slow, 1=Fast, 2=SuperFast)
    /// </summary>
    private void ApplyTimingPreset(int presetIndex)
    {
        switch (presetIndex)
        {
            case 0: // Slow - for slow PCs/load times
                Settings.BulkBuy.MouseMoveDelay.Value = 150;
                Settings.BulkBuy.PostClickDelay.Value = 300;
                Settings.BulkBuy.HideoutTokenDelay.Value = 300;
                Settings.BulkBuy.WindowCloseCheckInterval.Value = 100;
                Settings.BulkBuy.LoadingCheckInterval.Value = 200;
                Settings.BulkBuy.RetryDelay.Value = 500;
                Settings.BulkBuy.TimeoutPerItem.Value = 5;
                LogMessage("BulkBuy: Applied 'Slow' timing preset (for slow PCs/load times)");
                break;

            case 1: // Fast - normal/default
                Settings.BulkBuy.MouseMoveDelay.Value = 50;
                Settings.BulkBuy.PostClickDelay.Value = 150;
                Settings.BulkBuy.HideoutTokenDelay.Value = 150;
                Settings.BulkBuy.WindowCloseCheckInterval.Value = 50;
                Settings.BulkBuy.LoadingCheckInterval.Value = 100;
                Settings.BulkBuy.RetryDelay.Value = 300;
                Settings.BulkBuy.TimeoutPerItem.Value = 3;
                LogMessage("BulkBuy: Applied 'Fast' timing preset (normal/default)");
                break;

            case 2: // SuperFast - for fast PCs/SSD/quick load times
                Settings.BulkBuy.MouseMoveDelay.Value = 25;
                Settings.BulkBuy.PostClickDelay.Value = 100;
                Settings.BulkBuy.HideoutTokenDelay.Value = 100;
                Settings.BulkBuy.WindowCloseCheckInterval.Value = 25;
                Settings.BulkBuy.LoadingCheckInterval.Value = 50;
                Settings.BulkBuy.RetryDelay.Value = 200;
                Settings.BulkBuy.TimeoutPerItem.Value = 2;
                LogMessage("BulkBuy: Applied 'SuperFast' timing preset (for fast PCs/SSD)");
                break;
        }
    }
}

public partial class TradeUtils
{
    private void RenderBulkBuyGui()
    {
        if (!Settings.BulkBuy.Enable.Value) return;

        try
        {
            ImGui.SetNextWindowPos(Settings.BulkBuy.WindowPosition, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(520, 420), ImGuiCond.FirstUseEver);

            bool showGui = true;
            if (ImGui.Begin("TradeUtils - Bulk Buy", ref showGui,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
            {
                if (!showGui)
                {
                    if (_bulkBuyInProgress) StopBulkBuy();
                    Settings.BulkBuy.Enable.Value = false;
                    ImGui.End();
                    return;
                }

                Settings.BulkBuy.WindowPosition = ImGui.GetWindowPos();

                float windowWidth = ImGui.GetWindowWidth();
                const float buttonWidth = 80;
                ImGui.SetCursorPosX(windowWidth - buttonWidth - ImGui.GetStyle().WindowPadding.X);
                ImGui.SetCursorPosY(ImGui.GetStyle().WindowPadding.Y);

                if (ImGui.Button("Close", new Vector2(buttonWidth, 0)))
                {
                    if (_bulkBuyInProgress) StopBulkBuy();
                    Settings.BulkBuy.Enable.Value = false;
                }

                ImGui.Spacing();
                ImGui.TextColored(new Vector4(0.3f, 0.7f, 1.0f, 1.0f), "=== Bulk Item Buyer ===");
                ImGui.Spacing();

                RenderBulkBuyStatus();

                ImGui.Separator();
                ImGui.Spacing();

                RenderBulkBuySearchSummary();

                ImGui.Spacing();
                RenderBulkBuyControls();

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                RenderBulkBuyStatistics();

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.CollapsingHeader("Settings", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Indent();
                    RenderBulkBuySettings();
                    ImGui.Unindent();
                }

                if (_currentBulkBuyItem != null && ImGui.CollapsingHeader("Current Item"))
                {
                    ImGui.Indent();
                    ImGui.Text($"Name: {_currentBulkBuyItem.Name}");
                    ImGui.Text($"Price: {_currentBulkBuyItem.Price}");
                    ImGui.Text($"Seller: {_currentBulkBuyItem.AccountName}");
                    ImGui.Text($"Stash slot: ({_currentBulkBuyItem.X}, {_currentBulkBuyItem.Y})");
                    ImGui.Unindent();
                }

                if (ImGui.CollapsingHeader("Help & Info"))
                {
                    ImGui.Indent();
                    ImGui.TextWrapped("1. Open BulkBuy Settings and add a group, then a search inside it.");
                    ImGui.TextWrapped("2. Paste the trade search link into the search's 'Trade URL' box, e.g. " +
                                      "https://www.pathofexile.com/trade/search/Allflame/kyRr67a3u5");
                    ImGui.TextWrapped("3. Enable both the group and the search (shift-click their headers).");
                    ImGui.TextWrapped("4. Stand in your hideout with the currency you need in your inventory.");
                    ImGui.TextWrapped("5. Click 'Start Bulk Buy'.");
                    ImGui.Spacing();
                    ImGui.TextWrapped("It buys the cheapest matching listings first, one at a time, travelling to " +
                                      "each seller in turn. Before every click it reads the item out of the " +
                                      "seller's stash and refuses to buy anything that doesn't match the listing.");
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.0f, 1.0f), "Note:");
                    ImGui.TextWrapped("Automating trade actions is against GGG's terms of use and can get the " +
                                      "account banned. This is the same risk the auto-whisper and teleport " +
                                      "features carry.");
                    ImGui.Unindent();
                }

                if (Settings.LiveSearch.General.DebugMode.Value)
                {
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.TextColored(new Vector4(1.0f, 0.0f, 1.0f, 1.0f), "DEBUG INFO");
                    ImGui.Text($"Target {_bulkBuyTarget}, bought {_bulkBuyBought}, tried {_bulkBuyConsidered}");
                    ImGui.Text($"Owns input: {_bulkBuyOwnsInput}");
                    ImGui.Text($"Ctrl held: {_bulkBuyCtrlHeld}");
                    ImGui.Text($"Current: {_currentBulkBuyItem?.Name ?? "none"}");
                    if (_rateLimiter != null)
                        ImGui.Text($"Rate limit: {_rateLimiter.GetStatus()}");

                    if (ImGui.Button("Stash Now", new Vector2(100, 26)) && !_autoStashInProgress)
                        _ = StartAutoStashAsync();

                    if (_autoStashInProgress)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "Stashing...");
                    }
                }

                ImGui.End();
            }
        }
        catch (Exception ex)
        {
            LogError($"RenderBulkBuyGui error: {ex.Message}");
        }
    }

    private void RenderBulkBuyStatus()
    {
        if (_bulkBuyInProgress)
        {
            ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "RUNNING");
            ImGui.SameLine();
            ImGui.Text(_bulkBuyTarget > 0
                ? $"[bought {_bulkBuyBought}/{_bulkBuyTarget}, tried {_bulkBuyConsidered}]  {_bulkBuyStatus}"
                : _bulkBuyStatus);
        }
        else
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "STOPPED");

            // A run that ended on its own has to say so here. Previously the reason went to the
            // debug window and was gone as soon as it scrolled.
            if (!string.IsNullOrWhiteSpace(_bulkBuyStopReason))
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(1.0f, 0.75f, 0.3f, 1.0f), $"— {_bulkBuyStopReason}");
            }
        }
    }

    /// <summary>
    /// Shows what is actually configured, and says plainly when a search can't run. A search with
    /// no trade link used to fail only once the run started, deep in the log.
    /// </summary>
    private void RenderBulkBuySearchSummary()
    {
        var enabled = Settings.BulkBuy.Groups
            .Where(g => g.Enable.Value)
            .SelectMany(g => g.Searches.Where(s => s.Enable.Value))
            .ToList();

        ImGui.Text($"Groups: {Settings.BulkBuy.Groups.Count}    Enabled searches: {enabled.Count}");

        if (enabled.Count == 0)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                "No enabled searches — add one in BulkBuy Settings and shift-click to enable it.");
            return;
        }

        foreach (var search in enabled)
        {
            string url = search.TradeUrl?.Value?.Trim() ?? "";
            string json = search.QueryJson?.Value?.Trim() ?? "";
            string name = search.Name?.Value ?? "(unnamed)";

            if (!string.IsNullOrWhiteSpace(url))
            {
                if (TryParseTradeUrl(url, out var league, out var id))
                {
                    int wanted = search.MaxItems.Value;
                    ImGui.TextColored(
                        wanted > 0 ? new Vector4(0.6f, 0.85f, 0.6f, 1.0f) : new Vector4(0.6f, 0.6f, 0.6f, 1.0f),
                        wanted > 0
                            ? $"  • {name}: {league} / {id} — buy {wanted}"
                            : $"  • {name}: {league} / {id} — done");
                }
                else
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                        $"  • {name}: that Trade URL isn't a trade search link");
                }
            }
            else if (!string.IsNullOrWhiteSpace(json))
            {
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.5f, 1.0f),
                    $"  • {name}: raw Query JSON (advanced)");
            }
            else
            {
                ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                    $"  • {name}: no Trade URL set — paste the trade link into it");
            }
        }
    }

    private void RenderBulkBuyControls()
    {
        if (!_bulkBuyInProgress)
        {
            if (ImGui.Button("Start Bulk Buy", new Vector2(150, 30)))
                StartBulkBuy();
        }
        else
        {
            if (ImGui.Button("Stop Bulk Buy", new Vector2(150, 30)))
                StopBulkBuy();

            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.0f, 1.0f),
                _waitingForPurchaseWindow ? "Waiting for the trade window..." : _bulkBuyStatus);
        }
    }

    private void RenderBulkBuyStatistics()
    {
        ImGui.Text("Statistics:");
        ImGui.Indent();
        ImGui.Text($"Bought:  {Settings.BulkBuy.SuccessfulPurchases}");
        ImGui.Text($"Skipped: {_bulkBuySkipped}");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Listings that sold before arriving, or that didn't match what was listed.");

        // The breakdown, not just the total — "skipped 27" on its own says nothing about whether
        // the listings sold or the plugin failed to read them, and those need opposite fixes.
        if (_bulkBuySkipped > 0)
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.55f, 1.0f), $"   ({DescribeSkipBreakdown()})");

        ImGui.Text($"Failed:  {Settings.BulkBuy.FailedPurchases}");
        ImGui.Text($"Spent:   {_totalSpent}");

        if (!string.IsNullOrWhiteSpace(_bulkBuyLastSkipReason))
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.5f, 1.0f), $"Last skip: {_bulkBuyLastSkipReason}");

        if (_bulkBuyInProgress)
            ImGui.Text($"Time: {(DateTime.Now - _bulkBuyStartTime).TotalSeconds:F0}s");

        ImGui.Unindent();
    }

    private void RenderBulkBuySettings()
    {
        string session = Settings.LiveSearch.SessionId?.Value ?? string.Empty;
        if (ImGui.InputText("POESESSID (BulkBuy)", ref session, 128, ImGuiInputTextFlags.Password))
        {
            if (Settings.LiveSearch.SessionId == null)
                Settings.LiveSearch.SessionId = new TextNode(string.Empty);
            Settings.LiveSearch.SessionId.Value = session;
        }

        ImGui.Spacing();

        bool verify = Settings.BulkBuy.VerifyItemBeforeBuying.Value;
        if (ImGui.Checkbox("Check the item before buying##VerifyItem", ref verify))
            Settings.BulkBuy.VerifyItemBeforeBuying.Value = verify;
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Reads the item out of the seller's stash and refuses to click unless it matches the\n" +
                "listing: base type, identified state, rarity, corruption, size, item level, sockets\n" +
                "and stack size.\n\n" +
                "Leave this on. Sellers list identified and unidentified copies of the same unique\n" +
                "side by side, and a listing only records a stash coordinate — if the item moved or\n" +
                "sold, that coordinate now points at something else.");
        }

        if (!verify)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                "  Checks off: it will buy whatever sits in the listed slot.");
        }

        bool groupBySeller = Settings.BulkBuy.BuyMultipleFromSameSeller.Value;
        if (ImGui.Checkbox("Buy multiple from the same seller##GroupBySeller", ref groupBySeller))
            Settings.BulkBuy.BuyMultipleFromSameSeller.Value = groupBySeller;
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "When a seller has several matching listings, buy them all in one visit instead of\n" +
                "travelling back and forth.\n\n" +
                "Every item is still checked on its own before it's clicked — including that the\n" +
                "seller's open tab is the one that listing is in, which is what stops a slow tab\n" +
                "switch buying the wrong thing.");
        }

        int tolerance = Settings.BulkBuy.StopAfterConsecutiveFailures.Value;
        if (ImGui.SliderInt("Stop after N failures in a row##FailTolerance", ref tolerance, 1, 20))
            Settings.BulkBuy.StopAfterConsecutiveFailures.Value = tolerance;
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Listings that simply sold before you got there don't count towards this.\n" +
                             "Repeated real failures usually mean you've run out of the currency\n" +
                             "the listings are priced in.");
        }

        bool stopOnError = Settings.BulkBuy.StopOnError.Value;
        if (ImGui.Checkbox("Stop on Error##StopOnError", ref stopOnError))
            Settings.BulkBuy.StopOnError.Value = stopOnError;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Also stop the run the first time an item fails its check, instead of skipping it.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text("Pace");
        ImGui.Spacing();

        int pauseMin = Settings.BulkBuy.PurchasePauseMinSeconds.Value;
        int pauseMax = Settings.BulkBuy.PurchasePauseMaxSeconds.Value;

        if (ImGui.SliderInt("Pause between trades: min (s)##PauseMin", ref pauseMin, 0, 120))
        {
            Settings.BulkBuy.PurchasePauseMinSeconds.Value = pauseMin;
            if (Settings.BulkBuy.PurchasePauseMaxSeconds.Value < pauseMin)
                Settings.BulkBuy.PurchasePauseMaxSeconds.Value = pauseMin;
        }

        if (ImGui.SliderInt("Pause between trades: max (s)##PauseMax", ref pauseMax, 0, 300))
        {
            Settings.BulkBuy.PurchasePauseMaxSeconds.Value = pauseMax;
            if (Settings.BulkBuy.PurchasePauseMinSeconds.Value > pauseMax)
                Settings.BulkBuy.PurchasePauseMinSeconds.Value = pauseMax;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("How long to wait after finishing one listing before travelling to the\n" +
                             "next seller. A value is picked from this range each time.\n\n" +
                             "This is what controls how hard the run hits GGG's teleport endpoint.\n" +
                             "It is not protection from detection — see the note below.");
        }

        if (Settings.BulkBuy.PurchasePauseMaxSeconds.Value == 0)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f),
                "  No pause: this teleports again the instant a purchase completes.");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        string[] presetNames = { "Slow", "Fast", "SuperFast" };
        string currentPresetStr = Settings.BulkBuy.TimingPreset?.Value ?? "Fast";
        int currentPreset = Array.IndexOf(presetNames, currentPresetStr);
        if (currentPreset < 0) currentPreset = 1;

        if (ImGui.Combo("Timing Preset##TimingPreset", ref currentPreset, presetNames, presetNames.Length))
        {
            Settings.BulkBuy.TimingPreset.Value = presetNames[currentPreset];
            ApplyTimingPreset(currentPreset);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("How long to give the client to catch up — loading screens, the trade\n" +
                             "window rendering, the seller's tab switching.\n\n" +
                             "This is a reliability setting, not a pace setting. Use the pause above\n" +
                             "to slow the run down; use this only if the log says the trade window\n" +
                             "never opened.\n\n" +
                             "Slow: slow PC / long load times\nFast: normal (default)\nSuperFast: fast PC / SSD");
        }

        if (currentPreset == 2)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.0f, 1.0f),
                "  SuperFast only waits 2s for the trade window — expect false failures.");
        }
    }
}
