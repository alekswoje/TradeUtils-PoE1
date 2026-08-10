using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using TradeUtils.Utility;

namespace TradeUtils;

/// <summary>
/// Buys every listing behind a trade search link, one at a time.
///
/// The flow is deliberately flat: resolve the link into an ordered list of listings, then walk that
/// list. The previous version grouped listings by seller inside 10-item fetch batches and steered
/// itself with sentinel return values (-1 "batch stale", -2 "stop") and gotos, which made the two
/// exit paths unreachable in practice — the "refetch a stale batch" branch returned into a loop
/// that had already decided to stop, so stale items were silently skipped rather than retried.
///
/// Buying several listings from the same seller in one visit is intentionally NOT done here yet.
/// It is the obvious next step, but it needs the tab-switch to be confirmed before each click, and
/// getting that wrong buys the wrong item rather than merely wasting a teleport.
/// </summary>
public partial class TradeUtils
{
    /// <summary>How many items the run is meant to buy, summed across its searches.</summary>
    private int _bulkBuyTarget;

    /// <summary>Bought so far, which is what the target counts.</summary>
    private int _bulkBuyBought;

    /// <summary>Listings actually attempted — always ≥ bought, and the interesting gap.</summary>
    private int _bulkBuyConsidered;

    private int _bulkBuySkipped;

    private BulkBuyItem _currentBulkBuyItem;

    // Written from the render thread (hotkey, Stop button) and read by the buy task, so both need
    // to be volatile — otherwise Stop can be hoisted out of the task's loop conditions and the run
    // carries on clicking after the user has asked it not to.
    private volatile bool _bulkBuyInProgress;
    private volatile bool _bulkBuyPausedForFocus;

    private bool _waitingForPurchaseWindow;
    private DateTime _bulkBuyStartTime = DateTime.MinValue;
    private decimal _totalSpent;
    private string _bulkBuyStatus = "Idle";
    private string _bulkBuyLastSkipReason;
    private string _bulkBuyLastSkipCategory;

    /// <summary>
    /// Whether the last visit actually got as far as the seller's open trade window. Their other
    /// listings can only be switched to from there — and if the first item had already sold, the
    /// trip is still worth using for the rest.
    /// </summary>
    private bool _lastVisitReachedSeller;

    /// <summary>Why the last run ended, kept so the window can still say so afterwards.</summary>
    private string _bulkBuyStopReason;

    /// <summary>
    /// Skips tallied by cause.
    ///
    /// A bare "skipped: 27" is not a diagnosis — it lumps sold listings in with items that failed
    /// their identity check and with tabs that wouldn't read, which have completely different
    /// fixes. The breakdown is what says which one is actually happening.
    /// </summary>
    private readonly Dictionary<string, int> _bulkBuySkipsByReason = new Dictionary<string, int>();

    /// <summary>
    /// Records a skip under a short category and returns the outcome, so every skip site is one
    /// line and none of them can forget to categorise.
    /// </summary>
    /// <summary>
    /// Accounts for a search that attempted nothing, naming every way a candidate can be lost.
    ///
    /// A search matching 95 listings and trying none of them has to explain itself — the counters
    /// exist so the answer is read off rather than guessed at.
    /// </summary>
    private string DescribeWhyNothingWasBuyable(SearchPlan plan)
    {
        var parts = new List<string>();

        if (plan.DroppedNoToken > 0) parts.Add($"{plan.DroppedNoToken} had no hideout token (seller offline)");
        if (plan.DroppedNoAccount > 0) parts.Add($"{plan.DroppedNoAccount} had no seller account");
        if (plan.BlankEntries > 0) parts.Add($"{plan.BlankEntries} came back empty from the API (listing gone)");

        if (parts.Count == 0)
            return "and the counters are all zero, which means the fetches returned nothing at all. " +
                   "Check the messages above for a failed request.";

        string reason = string.Join(", ", parts);

        // The one cause people can act on, and the one that looks exactly like bad luck.
        if (plan.DroppedNoToken > 0 && plan.DroppedNoToken >= plan.BlankEntries)
            reason += ". If that's most of them, suspect the POESESSID — the trade API omits hideout " +
                      "tokens when the session isn't valid and gives no other sign";

        return reason + ".";
    }

    /// <summary>Skip counts as "sold 21, item didn't match 4, tab unreadable 2".</summary>
    private string DescribeSkipBreakdown()
    {
        lock (_bulkBuySkipsByReason)
        {
            if (_bulkBuySkipsByReason.Count == 0) return "none recorded";

            return string.Join(", ", _bulkBuySkipsByReason
                .OrderByDescending(pair => pair.Value)
                .Select(pair => $"{pair.Key} {pair.Value}"));
        }
    }

    private BuyOutcome SkipListing(PendingPurchase pending, string category, string detail)
    {
        _bulkBuyLastSkipReason = detail;
        _bulkBuyLastSkipCategory = category;

        lock (_bulkBuySkipsByReason)
        {
            _bulkBuySkipsByReason.TryGetValue(category, out int count);
            _bulkBuySkipsByReason[category] = count + 1;
        }

        LogMessage($"BulkBuy: skipping '{pending?.DisplayName}' — {detail}.");
        return BuyOutcome.Skipped;
    }

    private Task _bulkBuyTask;
    private CancellationTokenSource _bulkBuyCts;
    private bool _lastBulkBuyStartHotkeyState;

    /// <summary>Picks the gap between trades. Locked because Random isn't thread-safe.</summary>
    private readonly Random _bulkBuyPaceRandom = new Random();

    private readonly object _bulkBuyLogLock = new object();

    /// <summary>
    /// Logs a decision to the in-game window and to a file.
    ///
    /// The file is the point. Everything the plugin says goes to ExileCore's debug window and
    /// nowhere else, so once it scrolls away there is no record — a run that stopped on its own
    /// leaves no evidence of why, which is exactly the question people ask afterwards. Only the
    /// decision points are written, not every message, so the file stays a readable transcript.
    /// </summary>
    private void BulkLog(string message, bool isError = false)
    {
        if (isError) LogError(message);
        else LogMessage(message);

        try
        {
            var path = System.IO.Path.Combine(DirectoryFullName, $"bulkbuy-{DateTime.Now:yyyyMMdd}.log");
            var line = $"{DateTime.Now:HH:mm:ss} {(isError ? "ERR " : "    ")}{message}{Environment.NewLine}";

            lock (_bulkBuyLogLock)
                System.IO.File.AppendAllText(path, line);
        }
        catch
        {
            // Logging must never be the thing that breaks a run.
        }
    }

    /// <summary>What one listing attempt ended up doing.</summary>
    private enum BuyOutcome
    {
        /// <summary>Bought it.</summary>
        Bought,

        /// <summary>Deliberately not bought — sold, moved, or not the item that was listed.</summary>
        Skipped,

        /// <summary>Meant to buy it and couldn't. Counts toward the failure tolerance.</summary>
        Failed,

        /// <summary>Stop the whole run.</summary>
        Abort
    }

    partial void InitializeBulkBuy()
    {
        try
        {
            LogMessage("BulkBuy sub-plugin initialized");

            if (_rateLimiter == null)
                _rateLimiter = new QuotaGuard(LogMessage, LogError, () => LiveSearchSettings);

            var session = Settings.LiveSearch.SessionId?.Value ?? "";
            if (string.IsNullOrWhiteSpace(session))
                LogMessage("BulkBuy: no POESESSID set yet — add it in the Bulk Buy window before starting.");

            int searches = Settings.BulkBuy.Groups.SelectMany(g => g.Searches).Count();
            LogMessage($"BulkBuy config: {Settings.BulkBuy.Groups.Count} group(s), {searches} search(es).");
        }
        catch (Exception ex)
        {
            LogError($"Failed to initialize BulkBuy: {ex.Message}");
        }
    }

    partial void RenderBulkBuy() => RenderBulkBuyGui();

    partial void AreaChangeBulkBuy(AreaInstance area)
    {
        // Any zone change invalidates what we knew about whose hideout this is. Only the plugin's
        // own /hideout sets this back to true; everything else — a teleport to a seller, a portal,
        // the player walking somewhere — leaves it false so the stash routine travels home first.
        _inOwnHideout = false;
    }

    partial void DisposeBulkBuy()
    {
        try
        {
            _bulkBuyCts?.Cancel();
            _bulkBuyOwnsInput = false;
            ReleaseBulkBuyCtrl();
        }
        catch (Exception ex)
        {
            LogError($"Error disposing BulkBuy: {ex.Message}");
        }
    }

    partial void TickBulkBuy()
    {
        try
        {
            if (!GameController.Window.IsForeground())
            {
                if (_bulkBuyInProgress && !_bulkBuyPausedForFocus)
                {
                    LogMessage("BulkBuy: game lost focus, pausing (resumes automatically).");
                    _bulkBuyPausedForFocus = true;
                }

                return;
            }

            if (_bulkBuyInProgress && _bulkBuyPausedForFocus)
            {
                LogMessage("BulkBuy: game focused again, resuming.");
                _bulkBuyPausedForFocus = false;
            }

            var toggleKey = Settings.BulkBuy?.ToggleHotkey?.Value ?? Keys.None;
            if (toggleKey != Keys.None)
            {
                bool pressed = Input.GetKeyState(toggleKey);
                if (pressed && !_lastBulkBuyStartHotkeyState)
                {
                    if (_bulkBuyInProgress) StopBulkBuy();
                    else StartBulkBuy();
                }

                _lastBulkBuyStartHotkeyState = pressed;
            }

            if (Settings.LiveSearch.General.StopAllHotkey.Value != Keys.None &&
                Input.GetKeyState(Settings.LiveSearch.General.StopAllHotkey.Value) &&
                _bulkBuyInProgress)
            {
                StopBulkBuy();
            }

            // Note: no mouse handling here. The buy routine positions and clicks the cursor itself,
            // right after it has confirmed what is under it. A tick-driven "window opened, move the
            // mouse" hook (which is what this used to do) fires before that check has run.
        }
        catch (Exception ex)
        {
            LogError($"Error in BulkBuy tick: {ex.Message}");
        }
    }

    private void StartBulkBuy()
    {
        try
        {
            if (_bulkBuyInProgress)
            {
                LogMessage("BulkBuy: already running.");
                return;
            }

            if (!Settings.Enable.Value)
            {
                LogError("BulkBuy: the plugin is disabled — enable TradeUtils first.");
                return;
            }

            var sessionId = Settings.LiveSearch.SessionId?.Value ?? "";
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                LogError("BulkBuy: cannot start — no POESESSID. Set it in the Bulk Buy window.");
                return;
            }

            var searches = Settings.BulkBuy.Groups
                .Where(g => g.Enable.Value)
                .SelectMany(g => g.Searches.Where(s => s.Enable.Value))
                .ToList();

            if (searches.Count == 0)
            {
                LogError("BulkBuy: no enabled searches. Add a search, paste its trade link, and enable both it and its group.");
                return;
            }

            var unconfigured = searches
                .Where(s => string.IsNullOrWhiteSpace(s.TradeUrl?.Value) && string.IsNullOrWhiteSpace(s.QueryJson?.Value))
                .ToList();

            if (unconfigured.Count == searches.Count)
            {
                LogError("BulkBuy: every enabled search is missing its Trade URL. Paste the trade search link into each one.");
                return;
            }

            _bulkBuyTarget = 0;
            _bulkBuyBought = 0;
            _bulkBuyConsidered = 0;
            _bulkBuySkipped = 0;
            lock (_bulkBuySkipsByReason) _bulkBuySkipsByReason.Clear();
            _bulkBuyStopReason = null;
            _currentBulkBuyItem = null;
            _totalSpent = 0;
            _bulkBuyLastSkipReason = null;
            _bulkBuyLastSkipCategory = null;
            _bulkBuyCtrlHeld = false;
            Settings.BulkBuy.TotalItemsProcessed = 0;
            Settings.BulkBuy.SuccessfulPurchases = 0;
            Settings.BulkBuy.FailedPurchases = 0;
            Settings.BulkBuy.CurrentItemIndex = 0;

            _bulkBuyStartTime = DateTime.Now;
            _bulkBuyCts?.Cancel();
            _bulkBuyCts = new CancellationTokenSource();

            _bulkBuyInProgress = true;
            Settings.BulkBuy.IsRunning = true;
            _bulkBuyStatus = "Resolving searches";

            LogMessage($"BulkBuy: starting with {searches.Count} search(es).");

            _bulkBuyTask = Task.Run(() => RunBulkBuyLoopAsync(searches, sessionId, _bulkBuyCts.Token));
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: could not start — {ex.Message}");
            _bulkBuyInProgress = false;
            Settings.BulkBuy.IsRunning = false;
        }
    }

    private void StopBulkBuy()
    {
        try
        {
            _bulkBuyInProgress = false;
            _bulkBuyPausedForFocus = false;
            _bulkBuyOwnsInput = false;
            Settings.BulkBuy.IsRunning = false;
            _bulkBuyStatus = "Stopped";

            ReleaseBulkBuyCtrl();

            try
            {
                _bulkBuyCts?.Cancel();
            }
            catch
            {
                // Already disposed; nothing to cancel.
            }

            _currentBulkBuyItem = null;
            LogMessage("BulkBuy: stopped.");
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: error while stopping — {ex.Message}");
        }
    }

    private void ReleaseBulkBuyCtrl()
    {
        if (!_bulkBuyCtrlHeld) return;

        try
        {
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: could not release Ctrl — {ex.Message}");
        }
        finally
        {
            _bulkBuyCtrlHeld = false;
        }
    }

    private async Task RunBulkBuyLoopAsync(
        List<BulkBuySearch> searches,
        string sessionId,
        CancellationToken ct)
    {
        // Every exit below names itself, so the transcript always ends with a reason rather than
        // just stopping. "It stopped and I don't know why" is not a state this should be able to
        // reach.
        string stopReason = "unknown";

        try
        {
            _bulkBuyTarget = searches.Sum(s => Math.Max(0, s.MaxItems?.Value ?? 0));

            if (_bulkBuyTarget <= 0)
            {
                BulkLog("every enabled search is already complete — set 'Items to buy' above 0 on one of them.");
                stopReason = "nothing left to buy";
                return;
            }

            // One request, up front, so a dead session is named rather than discovered as
            // "every listing is unbuyable" twenty requests later.
            _bulkBuyStatus = "Checking your session";
            var account = await VerifyTradeSessionAsync(sessionId, ct);
            if (account == null)
            {
                stopReason = "POESESSID is no longer valid";
                BulkLog("your POESESSID has stopped working — GGG won't hand out hideout tokens with it, " +
                        "so nothing can be bought. Log in to pathofexile.com again and copy the new one.",
                        isError: true);
                return;
            }

            if (!string.IsNullOrEmpty(account))
                LogMessage($"BulkBuy: signed in as {account}.");

            BulkLog($"=== run started: buying up to {_bulkBuyTarget} item(s) across {searches.Count} search(es), " +
                    $"pause {Settings.BulkBuy.PurchasePauseMinSeconds.Value}-{Settings.BulkBuy.PurchasePauseMaxSeconds.Value}s, " +
                    $"verify={Settings.BulkBuy.VerifyItemBeforeBuying.Value}, " +
                    $"failure tolerance={Settings.BulkBuy.StopAfterConsecutiveFailures.Value} ===");
            _bulkBuyStatus = "Buying";

            int consecutiveFailures = 0;
            int consecutiveRateLimited = 0;
            int tolerance = Math.Max(1, Settings.BulkBuy.StopAfterConsecutiveFailures?.Value ?? 3);
            bool halt = false;
            int searchesRun = 0;

            // Each search is prepared when its turn comes, not all up front.
            //
            // Preparing them together fired two requests per search back to back, which walked
            // straight into GGG's ~5-searches-per-10s limit; the searches after the first came back
            // rate-limited and were dropped from the run without it stopping. Four enabled searches
            // ran as one. Preparing lazily spaces them out behind the inter-trade pause, and a
            // search that still fails only costs itself.
            foreach (var search in searches)
            {
                if (halt || !_bulkBuyInProgress || ct.IsCancellationRequested) break;

                string searchName = search.Name?.Value ?? "(unnamed)";
                int target = Math.Max(0, search.MaxItems?.Value ?? 0);

                if (target <= 0)
                {
                    BulkLog($"'{searchName}': already complete, nothing left to buy — moving on.");
                    continue;
                }

                _bulkBuyStatus = $"Running search {searchName}";

                var plan = await PrepareSearchAsync(search, target, sessionId, ct);
                if (plan == null)
                {
                    // Its own problem, not the run's. Say so and carry on to the next search.
                    BulkLog($"'{searchName}': couldn't run this search — moving on to the next one.", isError: true);
                    continue;
                }

                searchesRun++;
                int emptyTopUps = 0;

                // The target counts items BOUGHT. A listing that sold, failed its check or came from
                // an offline seller costs a candidate, not a purchase — so the loop keeps pulling
                // more candidates until it has bought what was asked for or run out of listings.
                while (plan.WantsMore && _bulkBuyInProgress && !ct.IsCancellationRequested)
                {
                    if (plan.Ready.Count == 0)
                    {
                        if (!plan.CandidatesLeft)
                        {
                            BulkLog($"'{plan.Name}': ran out of listings after buying {plan.Bought} of {plan.Target} " +
                                    $"({plan.Considered} candidate(s) tried, {plan.TotalMatched} matched the search).");

                            // Say why nothing was even attempted. Without this, a search whose
                            // listings were all discarded reads exactly like one that matched
                            // nothing, and the usual cause — a session that no longer returns
                            // hideout tokens — is invisible.
                            if (plan.Considered == 0)
                                BulkLog($"'{plan.Name}': nothing was attempted — {DescribeWhyNothingWasBuyable(plan)}",
                                        isError: true);

                            break;
                        }

                        _bulkBuyStatus = $"Fetching more listings for {plan.Name}";

                        if (await TopUpSearchPlanAsync(plan, sessionId, ct) == 0)
                        {
                            // A refused fetch doesn't consume its candidates, which is right — but
                            // it also means CandidatesLeft never falls, so retrying here is an
                            // infinite loop against a server that is already saying no.
                            if (_fetchBlocked)
                            {
                                stopReason = "the trade site is refusing our requests";
                                BulkLog("stopping — the trade site is refusing requests. Wait a few " +
                                        "minutes before running again, and raise the pause between trades.",
                                        isError: true);
                                halt = true;
                                break;
                            }

                            if (++emptyTopUps >= 3)
                            {
                                BulkLog($"'{plan.Name}': gave up fetching after {emptyTopUps} empty attempts.",
                                        isError: true);
                                break;
                            }

                            continue;
                        }

                        emptyTopUps = 0;
                    }

                    var pending = plan.TakeNext();
                    if (pending == null) continue;

                    plan.Considered++;
                    _bulkBuyConsidered++;

                    // Everything else already fetched from this same seller, so one trip can collect
                    // the lot. Capped at what's still wanted, so grouping can't overshoot the target.
                    var alsoFromSeller = (Settings.BulkBuy.BuyMultipleFromSameSeller?.Value ?? true)
                        ? await GatherSameSellerAsync(plan, pending, sessionId, ct)
                        : new List<PendingPurchase>();

                    if (alsoFromSeller.Count > 0)
                    {
                        // Buy out the tab we'll already be looking at before paging to another one.
                        alsoFromSeller = OrderForFewestTabSwitches(
                            alsoFromSeller, pending.Listing?.Listing?.Stash?.Name);

                        BulkLog($"{pending.SellerAccount} has {alsoFromSeller.Count + 1} matching listing(s) — " +
                                "buying them in one visit.");
                    }

                    while (_bulkBuyPausedForFocus && _bulkBuyInProgress && !ct.IsCancellationRequested)
                        await Task.Delay(250, ct);

                    if (!_bulkBuyInProgress || ct.IsCancellationRequested)
                    {
                        stopReason = "stopped by the user";
                        halt = true;
                        break;
                    }

                    // Make room. A single failed stash used to end the whole run, which is how a
                    // missed click on the stash turned into "bulk buy just stopped" — give it a
                    // couple of goes before writing the run off.
                    if (IsInventoryFullFor2x4Item())
                    {
                        bool haveRoom = false;

                        for (int stashAttempt = 1; stashAttempt <= 2 && !haveRoom; stashAttempt++)
                        {
                            if (!_bulkBuyInProgress || ct.IsCancellationRequested) break;

                            _bulkBuyStatus = "Stashing";
                            BulkLog($"inventory is full — stashing (attempt {stashAttempt}/2).");

                            haveRoom = await CheckAndTriggerAutoStashAsync();

                            if (!haveRoom && stashAttempt < 2)
                                await Task.Delay(1500, ct);
                        }

                        if (!haveRoom)
                        {
                            stopReason = "could not free up inventory space (the stash never opened)";
                            BulkLog("couldn't free up inventory space — stopping. " +
                                    "Check whether the stash actually opened.", isError: true);
                            halt = true;
                            break;
                        }
                    }

                    // Outcome accounting is shared by the first purchase of a visit and every
                    // extra bought from the same seller, so it lives in one place.
                    void RecordOutcome(PendingPurchase item, BuyOutcome outcome)
                    {
                    Settings.BulkBuy.TotalItemsProcessed++;
                    string progress = $"[{plan.Name} {plan.Bought + (outcome == BuyOutcome.Bought ? 1 : 0)}/{plan.Target}]";

                    switch (outcome)
                    {
                        case BuyOutcome.Bought:
                            consecutiveFailures = 0;
                            plan.Bought++;
                            _bulkBuyBought++;
                            Settings.BulkBuy.SuccessfulPurchases++;
                            Settings.BulkBuy.CurrentItemIndex++;
                            if (item.Listing?.Listing?.Price != null)
                                _totalSpent += item.Listing.Listing.Price.Amount;

                            // Count it off the search's remaining total straight away, not at the
                            // end. A run that is stopped or dies halfway should still leave the
                            // setting reflecting what was actually bought.
                            if (search.MaxItems != null)
                                search.MaxItems.Value = Math.Max(0, search.MaxItems.Value - 1);

                            BulkLog($"{progress} BOUGHT '{item.DisplayName}' " +
                                    $"for {item.PriceText} from {item.SellerAccount}");
                            break;

                        case BuyOutcome.Skipped:
                            // Doesn't count against the target or the failure tolerance — a listing
                            // that sold before we arrived isn't a malfunction and isn't a purchase.
                            // The loop just pulls the next candidate.
                            _bulkBuySkipped++;

                            // Rate limiting is the exception: it isn't about this listing, so
                            // marching through the rest of the candidate list gets nowhere and
                            // burns it. Give it a couple of goes, then stop.
                            if (_bulkBuyLastSkipCategory == "rate limited")
                            {
                                consecutiveRateLimited++;
                                if (consecutiveRateLimited >= 3)
                                {
                                    stopReason = "rate limited by GGG";
                                    BulkLog("rate limited three times in a row — stopping. " +
                                            "Wait a few minutes, and raise the pause between trades.",
                                            isError: true);
                                    halt = true;
                                }
                            }
                            else
                            {
                                consecutiveRateLimited = 0;
                            }

                            BulkLog($"{progress} skipped '{item.DisplayName}' " +
                                    $"— {_bulkBuyLastSkipReason ?? "see above"}");
                            break;

                        case BuyOutcome.Failed:
                            consecutiveFailures++;
                            Settings.BulkBuy.FailedPurchases++;
                            BulkLog($"{progress} FAILED '{item.DisplayName}' from {item.SellerAccount} " +
                                    $"({consecutiveFailures}/{tolerance} in a row)", isError: true);
                            break;

                        case BuyOutcome.Abort:
                            stopReason = "aborted at this listing";
                            BulkLog($"{progress} aborted on '{item.DisplayName}'");
                            halt = true;
                            break;
                    }

                    if (!halt && consecutiveFailures >= tolerance)
                    {
                        stopReason = $"{consecutiveFailures} purchases failed in a row";
                        BulkLog($"{consecutiveFailures} purchases failed in a row — stopping. " +
                                "Usually this means you're out of the currency the listings are priced in.",
                                isError: true);
                        halt = true;
                    }
                    }

                    // --- the visit -------------------------------------------------------------
                    RecordOutcome(pending, await BuyOneListingAsync(pending, sessionId, ct));
                    bool reachedSeller = _lastVisitReachedSeller;

                    if (!halt) await CheckAndTriggerAutoStashAsync();

                    // Collect the seller's other listings while we're standing there. Worth doing
                    // even if the first one had sold — the trip is already paid for.
                    int extraIndex = 0;
                    if (reachedSeller)
                    {
                        for (; extraIndex < alsoFromSeller.Count; extraIndex++)
                        {
                            // Re-read rather than trusting the snapshot: a switch that failed
                            // clears this, and the rest of the seller's items are then unreachable.
                            if (halt || !plan.WantsMore || !_lastVisitReachedSeller ||
                                !_bulkBuyInProgress || ct.IsCancellationRequested)
                                break;

                            // No pause here. Buying the next item from a seller we're already
                            // standing in front of costs nothing at GGG's end when it's in the same
                            // tab, and BuySameSellerListingAsync paces itself in the one case that
                            // does make a request — paging the window to a different tab.
                            var extra = alsoFromSeller[extraIndex];
                            plan.Considered++;
                            _bulkBuyConsidered++;

                            RecordOutcome(extra, await BuySameSellerListingAsync(extra, sessionId, ct));

                            if (!halt) await CheckAndTriggerAutoStashAsync();
                        }
                    }

                    // Anything not reached goes back at the front of the queue rather than being
                    // dropped — grouping must never lose listings it decided not to attempt.
                    if (extraIndex < alsoFromSeller.Count)
                        plan.Ready.InsertRange(0, alsoFromSeller.Skip(extraIndex));

                    if (halt) break;

                    // Pace the next trade. Every outcome above already spent a teleport, so the
                    // pause applies whether the purchase succeeded or not — it is the trade rate
                    // that matters, not the success rate.
                    if (plan.WantsMore) await PauseBetweenTradesAsync(ct);
                }

                // Close the search out. Finishing one is not a reason to stop the run — the loop
                // simply moves to the next search.
                int stillWanted = Math.Max(0, search.MaxItems?.Value ?? 0);

                if (stillWanted == 0 && plan.Bought > 0)
                {
                    if (search.Enable != null) search.Enable.Value = false;
                    BulkLog($"'{plan.Name}': bought all {plan.Target} requested — search switched off.");
                }
                else if (plan.Bought > 0)
                {
                    BulkLog($"'{plan.Name}': bought {plan.Bought} of {plan.Target}; {stillWanted} left for next time.");
                }
            }

            if (searchesRun == 0)
            {
                stopReason = "none of the enabled searches could be run";
                BulkLog("no search could be run — see the messages above.", isError: true);
            }
            else if (!halt)
            {
                // A stop requested while fetching leaves the inner loops via their conditions
                // rather than by throwing, so it has to be recognised here — otherwise pressing
                // Stop gets reported as "ran out of listings".
                if (!_bulkBuyInProgress || ct.IsCancellationRequested)
                    stopReason = "stopped by the user";
                else
                    stopReason = _bulkBuyBought >= _bulkBuyTarget
                        ? $"bought everything asked for ({_bulkBuyBought}/{_bulkBuyTarget})"
                        : $"ran out of listings ({_bulkBuyBought}/{_bulkBuyTarget} bought)";
            }
        }
        catch (OperationCanceledException)
        {
            stopReason = "stopped by the user";
        }
        catch (Exception ex)
        {
            stopReason = $"unhandled error — {ex.Message}";
            BulkLog($"unhandled error — {ex}", isError: true);
        }
        finally
        {
            _bulkBuyOwnsInput = false;
            ReleaseBulkBuyCtrl();
            _bulkBuyInProgress = false;
            _currentBulkBuyItem = null;
            Settings.BulkBuy.IsRunning = false;

            _bulkBuyStopReason = stopReason;
            _bulkBuyStatus = $"Stopped: {stopReason}";

            BulkLog($"=== run ended: {stopReason}. " +
                    $"Bought {Settings.BulkBuy.SuccessfulPurchases}/{_bulkBuyTarget}, " +
                    $"tried {_bulkBuyConsidered}, " +
                    $"skipped {_bulkBuySkipped}, " +
                    $"failed {Settings.BulkBuy.FailedPurchases}, " +
                    $"spent {_totalSpent} ===");

            if (_bulkBuySkipped > 0)
                BulkLog($"    skips by cause: {DescribeSkipBreakdown()}");
        }
    }

    /// <summary>
    /// Waits between trades, for a duration picked from the configured range.
    ///
    /// This is pacing, not camouflage. It keeps the run from firing teleport requests back to back
    /// — which is what the whisper endpoint's own rate limit is there to stop — and a range rather
    /// than a fixed number avoids the run marching in lockstep with anything. It does not make the
    /// automation undetectable, and nothing here should be sold to users as if it does.
    /// </summary>
    private async Task PauseBetweenTradesAsync(CancellationToken ct)
    {
        int min = Math.Max(0, Settings.BulkBuy.PurchasePauseMinSeconds?.Value ?? 5);
        int max = Math.Max(min, Settings.BulkBuy.PurchasePauseMaxSeconds?.Value ?? 12);
        if (max <= 0) return;

        int seconds;
        lock (_bulkBuyPaceRandom)
            seconds = _bulkBuyPaceRandom.Next(min, max + 1);

        if (seconds <= 0) return;

        LogMessage($"BulkBuy: waiting {seconds}s before the next trade.");

        // Counted down a second at a time so the window shows why nothing is happening, and so Stop
        // takes effect immediately rather than at the end of the wait.
        for (int remaining = seconds; remaining > 0; remaining--)
        {
            if (!_bulkBuyInProgress || ct.IsCancellationRequested) return;

            _bulkBuyStatus = $"Waiting {remaining}s before the next trade";
            await Task.Delay(1000, ct);
        }
    }

    /// <summary>
    /// Teleports to the seller, confirms the item in the slot is the one that was listed, and buys
    /// it. Every exit path says why.
    /// </summary>
    private async Task<BuyOutcome> BuyOneListingAsync(
        PendingPurchase pending,
        string sessionId,
        CancellationToken ct)
    {
        _currentBulkBuyItem = new BulkBuyItem
        {
            Name = pending.DisplayName,
            Price = pending.PriceText,
            HideoutToken = pending.HideoutToken,
            ItemId = pending.ItemId,
            AccountName = pending.SellerAccount,
            IsOnline = true,
            X = pending.StashX,
            Y = pending.StashY,
            AddedTime = DateTime.Now,
            Status = "Buying"
        };

        LogMessage($"BulkBuy [{_bulkBuyBought}/{_bulkBuyTarget} bought]: trying '{pending.DisplayName}' " +
                   $"for {pending.PriceText} from {pending.SellerAccount}.");

        _bulkBuyOwnsInput = true;
        _lastVisitReachedSeller = false;

        try
        {
            _bulkBuyStatus = $"Travelling to {pending.SellerAccount}";

            // Tokens for the whole queue are minted when the run starts, so by the time a long run
            // reaches the back of the list they have aged out. Renew before spending the teleport
            // rather than letting it fail and calling the listing sold.
            if (pending.TokenExpiringWithin(TimeSpan.FromSeconds(60)))
            {
                LogDebug($"BulkBuy: token for '{pending.DisplayName}' is stale, refreshing.");

                var renew = await RefreshListingAsync(pending, sessionId, ct);
                if (renew == RefreshResult.Gone)
                    return SkipListing(pending, "sold", "the listing is gone from the trade site");
            }

            var travel = await SendHideoutTokenAsync(pending, sessionId, ct);

            if (travel == TravelResult.RateLimited)
                return SkipListing(pending, "rate limited", "no teleport quota left");

            if (travel == TravelResult.Expired)
            {
                // The whisper failed the way a sold listing does — but an expired token fails
                // identically, so ask the API which it was instead of assuming.
                var recheck = await RefreshListingAsync(pending, sessionId, ct);

                if (recheck == RefreshResult.Gone)
                    return SkipListing(pending, "sold", "the listing sold before we got there");

                if (recheck == RefreshResult.Refreshed)
                {
                    LogMessage($"BulkBuy: '{pending.DisplayName}' is still listed — the token had expired. Retrying.");
                    travel = await SendHideoutTokenAsync(pending, sessionId, ct);

                    if (travel == TravelResult.Expired)
                        return SkipListing(pending, "sold", "still unreachable after a fresh token");
                }
                else
                {
                    return SkipListing(pending, "unreachable",
                        "couldn't tell whether the listing is still there (seller offline, or the API didn't answer)");
                }
            }

            if (travel != TravelResult.Ok)
            {
                LogError($"BulkBuy: could not reach {pending.SellerAccount} for '{pending.DisplayName}'.");
                return BuyOutcome.Failed;
            }

            _bulkBuyStatus = $"Loading into {pending.SellerAccount}'s hideout";

            if (!await WaitForLoadingToFinishAsync(ct))
            {
                LogError($"BulkBuy: still on a loading screen after 15s for '{pending.DisplayName}'.");
                return BuyOutcome.Failed;
            }

            _bulkBuyStatus = "Waiting for the trade window";
            int timeoutMs = (Settings.BulkBuy.TimeoutPerItem?.Value ?? 3) * 1000;
            if (!await WaitForPurchaseWindowAsync(timeoutMs, ct))
            {
                LogError($"BulkBuy: the trade window never opened for '{pending.DisplayName}' " +
                         $"(waited {timeoutMs / 1000}s). Try the 'Slow' timing preset if this keeps happening.");
                return BuyOutcome.Failed;
            }

            // We are standing in the seller's hideout with their stash open, so their other
            // listings are now reachable without travelling again.
            _lastVisitReachedSeller = true;

            return await VerifyAndBuyAsync(pending, ct);
        }
        catch (OperationCanceledException)
        {
            return BuyOutcome.Abort;
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: error buying '{pending.DisplayName}' — {ex.Message}");
            return BuyOutcome.Failed;
        }
        finally
        {
            _bulkBuyOwnsInput = false;
            ReleaseBulkBuyCtrl();
        }
    }

    /// <summary>
    /// Buys another listing from the seller whose hideout we are already standing in.
    ///
    /// Sending the listing's hideout token while already there switches the trade window to that
    /// item's tab instead of teleporting — the same call the trade site makes, and the reason
    /// <c>continue: true</c> is on the request (it skips the site's "Item is in demand" ask-twice
    /// flow, which would otherwise swallow the first attempt).
    ///
    /// The danger unique to this path is acting before the tab switch has landed, when the
    /// coordinate still points into the previous tab. Nothing here relies on a delay being long
    /// enough: <see cref="VerifyAndBuyAsync"/> waits for the client to positively show the right
    /// item in the right tab, and the tab name is a hard check.
    /// </summary>
    private async Task<BuyOutcome> BuySameSellerListingAsync(
        PendingPurchase pending,
        string sessionId,
        CancellationToken ct)
    {
        _currentBulkBuyItem = new BulkBuyItem
        {
            Name = pending.DisplayName,
            Price = pending.PriceText,
            ItemId = pending.ItemId,
            AccountName = pending.SellerAccount,
            IsOnline = true,
            X = pending.StashX,
            Y = pending.StashY,
            AddedTime = DateTime.Now,
            Status = "Buying"
        };

        LogMessage($"BulkBuy: same seller — switching to '{pending.DisplayName}' for {pending.PriceText}.");

        _bulkBuyOwnsInput = true;

        try
        {
            _bulkBuyStatus = $"Next item from {pending.SellerAccount}";

            // Already looking at the tab this item is in: no request to make, so nothing to pace
            // and no token to refresh. Verify it and click it.
            if (IsListingTabAlreadyOpen(pending.Listing))
            {
                LogDebug($"BulkBuy: '{pending.DisplayName}' is in the tab already open — buying it directly.");
                return await VerifyAndBuyAsync(pending, ct);
            }

            // Different tab, so the token has to be sent to page the window across. That is a real
            // request against the teleport quota, which is the only reason to pace here at all.
            await PauseBetweenTradesAsync(ct);
            if (!_bulkBuyInProgress || ct.IsCancellationRequested) return BuyOutcome.Abort;

            if (pending.TokenExpiringWithin(TimeSpan.FromSeconds(60)))
            {
                var renew = await RefreshListingAsync(pending, sessionId, ct);
                if (renew == RefreshResult.Gone)
                    return SkipListing(pending, "sold", "the listing is gone from the trade site");
            }

            var travel = await SendHideoutTokenAsync(pending, sessionId, ct);

            if (travel == TravelResult.RateLimited)
                return SkipListing(pending, "rate limited", "no teleport quota left");

            if (travel == TravelResult.Expired)
            {
                var recheck = await RefreshListingAsync(pending, sessionId, ct);
                if (recheck == RefreshResult.Gone)
                    return SkipListing(pending, "sold", "the listing sold before we got to it");

                if (recheck != RefreshResult.Refreshed)
                    return SkipListing(pending, "unreachable", "couldn't confirm the listing is still there");

                travel = await SendHideoutTokenAsync(pending, sessionId, ct);
                if (travel == TravelResult.Expired)
                    return SkipListing(pending, "sold", "still unreachable after a fresh token");
            }

            if (travel != TravelResult.Ok)
            {
                LogError($"BulkBuy: couldn't switch to '{pending.DisplayName}' at {pending.SellerAccount}.");
                return BuyOutcome.Failed;
            }

            // Switching shouldn't reload the zone, but a seller can boot us between items.
            if (!await WaitForLoadingToFinishAsync(ct)) return BuyOutcome.Failed;

            int timeoutMs = (Settings.BulkBuy.TimeoutPerItem?.Value ?? 3) * 1000;
            if (!await WaitForPurchaseWindowAsync(timeoutMs, ct))
            {
                LogError($"BulkBuy: the trade window closed while switching to '{pending.DisplayName}'.");
                _lastVisitReachedSeller = false;
                return BuyOutcome.Failed;
            }

            return await VerifyAndBuyAsync(pending, ct);
        }
        catch (OperationCanceledException)
        {
            return BuyOutcome.Abort;
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: error buying '{pending.DisplayName}' — {ex.Message}");
            return BuyOutcome.Failed;
        }
        finally
        {
            _bulkBuyOwnsInput = false;
            ReleaseBulkBuyCtrl();
        }
    }

    /// <summary>
    /// The safety gate. Confirms the slot holds the listed item, then clicks it and confirms the
    /// item actually left the slot.
    /// </summary>
    private async Task<BuyOutcome> VerifyAndBuyAsync(PendingPurchase pending, CancellationToken ct)
    {
        _bulkBuyStatus = "Checking the item";

        bool verify = Settings.BulkBuy.VerifyItemBeforeBuying?.Value ?? true;

        // Wait for the client to actually be showing this listing rather than assuming a fixed
        // delay was long enough. This is what makes switching between one seller's items safe: the
        // tab change is not instant, and until it lands the coordinate points into the old tab.
        int settleMs = Math.Max(2000, (Settings.BulkBuy.HideoutTokenDelay?.Value ?? 150) * 10);
        var check = await WaitForListingInSlotAsync(pending.Listing, pending.StashX, pending.StashY, settleMs, ct);

        // A slot that couldn't be resolved at all is a different problem from an item that didn't
        // match, and it's the one that needs evidence to fix. Dump what the client is reporting.
        if ((check.Slot == null || !check.Slot.Ok) && Settings.LiveSearch.General.DebugMode.Value)
            LogPurchaseWindowDiagnostics(pending.StashX, pending.StashY);

        if (!check.Matches)
        {
            if (verify)
            {
                LogError($"BulkBuy: NOT buying '{pending.DisplayName}' — {check.Reason}.");

                if (Settings.BulkBuy.StopOnError?.Value ?? false)
                {
                    _bulkBuyLastSkipReason = check.Reason;
                    return BuyOutcome.Abort;
                }

                // Separate categories because they need separate fixes: an item that read fine but
                // didn't match is a stale listing, whereas a slot that wouldn't resolve at all is
                // the plugin failing to read the tab.
                bool slotUnreadable = check.Slot == null || !check.Slot.Ok;
                return SkipListing(pending, slotUnreadable ? "tab unreadable" : "item didn't match", check.Reason);
            }

            // Verification off: the click still needs a real slot to aim at, so an unresolved slot
            // is still fatal to this listing — there is no "buy blind" mode.
            if (check.Slot == null || !check.Slot.Ok)
                return SkipListing(pending, "tab unreadable", check.Reason);

            LogMessage($"BulkBuy: ⚠ verification is off and this item doesn't match its listing ({check.Reason}). Buying anyway.");
        }
        else
        {
            LogMessage($"BulkBuy: verified '{pending.DisplayName}' — {check.Checked}.");
        }

        var slot = check.Slot;
        if (slot == null || !slot.Ok)
            return SkipListing(pending, "tab unreadable", check.Reason ?? "the slot couldn't be resolved");

        if (!slot.HasClickPoint)
        {
            LogError($"BulkBuy: found '{pending.DisplayName}' in slot ({pending.StashX},{pending.StashY}) " +
                     "but can't work out where it's drawn on screen, so it won't be clicked.");
            return BuyOutcome.Failed;
        }

        var approved = slot;

        _bulkBuyStatus = "Buying";

        // Arriving in a hideout and clicking immediately can beat the client to loading the
        // player's own inventory, and the game then rejects the trade as "you do not have enough
        // currency" even though you do. An unreadable signature is that state, so wait it out.
        await WaitForInventoryReadableAsync(ct);

        // Two attempts, because that rejection leaves the item exactly where it was — nothing was
        // bought and nothing moved. The retry is only ever reached when the item is confirmed still
        // in its slot below, so it cannot turn one purchase into two.
        const int maxAttempts = 2;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (!_bulkBuyInProgress || ct.IsCancellationRequested) return BuyOutcome.Abort;

            await MoveMouseToSlotAsync(approved, ct);
            await Task.Delay(Settings.BulkBuy.MouseMoveDelay?.Value ?? 50, ct);

            if (GameController.IsLoading || !IsPurchaseWindowOpen())
                return SkipListing(pending, "window closed", "the trade window closed before the click");

            if (verify)
            {
                var recheck = ReconfirmSlot(pending.Listing, pending.StashX, pending.StashY, approved);

                if (!recheck.Matches)
                {
                    // On a retry this is the important branch: the item is no longer the one we
                    // approved, so the previous click may well have bought it. Never click again.
                    if (attempt > 1)
                    {
                        LogMessage($"BulkBuy: '{pending.DisplayName}' is no longer in its slot after the " +
                                   "first click — not clicking again.");
                        return BuyOutcome.Failed;
                    }

                    LogError($"BulkBuy: NOT buying '{pending.DisplayName}' — {recheck.Reason}.");
                    return SkipListing(pending, "slot changed", recheck.Reason);
                }

                // If the tab re-seated its items the element moved with them, so aim at where the
                // item is drawn now rather than where it was a moment ago.
                if (recheck.Slot != null && recheck.Slot.Ok)
                {
                    if (recheck.Slot.ItemAddress != approved.ItemAddress && recheck.Slot.HasClickPoint)
                    {
                        approved = recheck.Slot;
                        await MoveMouseToSlotAsync(approved, ct);
                        await Task.Delay(Settings.BulkBuy.MouseMoveDelay?.Value ?? 50, ct);
                    }
                    else
                    {
                        approved = recheck.Slot;
                    }
                }
            }

            // Snapshot the inventory before the click, not after — this is the "did anything
            // arrive" half of the purchase check, and taken afterwards it compares the post-click
            // state to itself and can never differ.
            string signatureBefore = GetInventorySignature();

            await PerformCtrlLeftClickAsync();

            var result = await ConfirmPurchaseAsync(pending, approved, signatureBefore, ct);
            if (result != BuyOutcome.Failed || attempt == maxAttempts) return result;

            // Only retry when checks are on. Without them there is no way to confirm the item is
            // still sitting there, and clicking again on an unknown state is how you buy twice.
            if (!verify)
            {
                LogMessage("BulkBuy: not retrying — item checks are switched off, so a second click " +
                           "can't be shown to be safe.");
                return result;
            }

            // ConfirmPurchaseAsync only reports Failed with the item still sitting in its slot, so
            // nothing was bought. Give the client a moment and have one more go.
            LogMessage($"BulkBuy: '{pending.DisplayName}' didn't go through — retrying in 2.5s " +
                       "(the client may still have been loading).");
            await Task.Delay(2500, ct);
        }

        return BuyOutcome.Failed;
    }

    /// <summary>
    /// Waits for the player's own inventory to become readable, which is the state the game checks
    /// when it decides whether you can afford a trade. Best-effort: proceeds either way.
    /// </summary>
    private async Task WaitForInventoryReadableAsync(CancellationToken ct)
    {
        var deadline = DateTime.Now.AddMilliseconds(2500);

        while (DateTime.Now < deadline && _bulkBuyInProgress && !ct.IsCancellationRequested)
        {
            if (!GameController.IsLoading && !string.IsNullOrEmpty(GetInventorySignature())) return;
            await Task.Delay(150, ct);
        }
    }

    /// <summary>
    /// Decides whether the click actually bought anything.
    ///
    /// The strong signal is the listed item leaving its slot; a changed inventory signature is only
    /// corroborating, because currency leaving the inventory to pay for something also changes it.
    /// An item on the cursor means the click picked it up instead of buying it — that is a failure,
    /// and the item has to be put back down before anything else happens.
    /// </summary>
    private async Task<BuyOutcome> ConfirmPurchaseAsync(
        PendingPurchase pending,
        PurchaseSlot approved,
        string signatureBefore,
        CancellationToken ct)
    {
        int postClickDelay = Settings.BulkBuy.PostClickDelay?.Value ?? 150;

        for (int check = 1; check <= 5; check++)
        {
            await Task.Delay(postClickDelay, ct);

            if (IsItemOnCursor())
            {
                LogError($"BulkBuy: '{pending.DisplayName}' ended up on the cursor instead of being bought. Putting it back.");
                await DropItemFromCursorAsync();
                return BuyOutcome.Failed;
            }

            // "The slot is empty" only means anything while the window is still open. Once it
            // closes there is no seller inventory to read, so every lookup returns null and an
            // unconditional check here would report a purchase that never happened.
            if (IsPurchaseWindowOpen())
            {
                var stillThere = ResolvePurchaseSlot(pending.StashX, pending.StashY, approved?.InventoryAddress ?? 0);

                // The slot emptying is the signal. A changed entity address is NOT — buying an item
                // rebuilds the tab, which re-seats everything still in it, so treating "different
                // address" as "bought" would call a failed click a success. When the address moves
                // but something is still there, re-verify: if it no longer matches the listing the
                // item really did leave, otherwise it never went anywhere.
                bool itemGone = !stillThere.Ok;

                if (!itemGone && approved != null &&
                    stillThere.ItemAddress != 0 && stillThere.ItemAddress != approved.ItemAddress)
                {
                    var recheck = VerifyListingInSlot(pending.Listing, pending.StashX, pending.StashY,
                        approved.InventoryAddress);
                    itemGone = !recheck.Matches;
                }

                if (itemGone)
                {
                    LogMessage($"BulkBuy: ✅ bought '{pending.DisplayName}' for {pending.PriceText}.");
                    return BuyOutcome.Bought;
                }
            }

            string signatureNow = GetInventorySignature();
            if (!string.IsNullOrEmpty(signatureBefore) &&
                !string.IsNullOrEmpty(signatureNow) &&
                signatureNow != signatureBefore)
            {
                LogMessage($"BulkBuy: ✅ bought '{pending.DisplayName}' for {pending.PriceText} (inventory changed).");
                return BuyOutcome.Bought;
            }

            LogDebug($"BulkBuy: post-click check {check}/5 for '{pending.DisplayName}' — item still in the slot.");
        }

        LogError($"BulkBuy: the click on '{pending.DisplayName}' did nothing. " +
                 "Most often that's not having enough of the currency it's priced in.");
        return BuyOutcome.Failed;
    }

    /// <summary>
    /// Puts the cursor on the point the slot lookup worked out — the item's own drawn rectangle
    /// where the client exposes it, otherwise grid arithmetic over the tab's real column and row
    /// count.
    ///
    /// Neither path assumes the fixed 12x12 grid the rest of the plugin uses, which aims at roughly
    /// double-scale coordinates whenever a seller lists out of a 24x24 quad tab.
    /// </summary>
    private async Task MoveMouseToSlotAsync(PurchaseSlot slot, CancellationToken ct)
    {
        if (!CanSendInput("move the cursor to the item")) return;

        try
        {
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point(slot.ClickX, slot.ClickY);
            LogDebug($"BulkBuy: cursor to ({slot.ClickX},{slot.ClickY}) via {slot.ClickSource}.");

            await Task.Delay(20, ct);
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: could not position the cursor — {ex.Message}");
        }
    }

    private enum TravelResult
    {
        Ok,

        /// <summary>The listing is gone, or its token no longer works.</summary>
        Expired,

        /// <summary>No teleport quota. Not the listing's fault, and retrying immediately won't help.</summary>
        RateLimited,

        Failed
    }

    /// <summary>
    /// Sends the listing's hideout token, which is what actually teleports the character and opens
    /// the seller's tab on the right page.
    ///
    /// Note the request headers here are left exactly as the plugin's author wrote them, unlike the
    /// read-only search path which identifies itself as TradeUtils. Changing this request either way
    /// is the user's call, not a side effect of fixing bulk buy.
    /// </summary>
    private async Task<TravelResult> SendHideoutTokenAsync(
        PendingPurchase pending,
        string sessionId,
        CancellationToken ct)
    {
        const string teleportScope = "whisper";

        try
        {
            // Polls and shows a countdown rather than sleeping blind, so this can't sit for the
            // full 30s when the window reopens after two.
            if (!await WaitForQuotaAsync(teleportScope, "teleport", ct))
            {
                LogError($"BulkBuy: still out of teleport quota after {MaxQuotaWaitMs / 1000}s — " +
                         $"{_rateLimiter?.GetStatus(teleportScope)}. Skipping this listing rather than " +
                         "hammering the endpoint.");
                return TravelResult.RateLimited;
            }

            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://www.pathofexile.com/api/trade/whisper"))
            {
                request.Content = new StringContent(
                    $"{{ \"token\": \"{pending.HideoutToken}\", \"continue\": true }}",
                    Encoding.UTF8,
                    "application/json");

                request.Headers.Add("Cookie", $"POESESSID={sessionId}");
                request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36");
                request.Headers.Add("Accept", "*/*");
                request.Headers.Add("Accept-Encoding", "gzip, deflate, br, zstd");
                request.Headers.Add("Accept-Language", "en-US,en;q=0.9");
                request.Headers.Add("Priority", "u=1, i");
                request.Headers.Add("Referer", $"https://www.pathofexile.com/trade/search/{Uri.EscapeDataString(pending.League ?? "")}");
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");

                using (var response = await _httpClient.SendAsync(request, ct))
                {
                    if (_rateLimiter != null)
                    {
                        _rateLimiter.ParseRateLimitHeaders(response);
                        if (await _rateLimiter.HandleRateLimitResponse(response) > 0)
                        {
                            LogError("BulkBuy: rate limited on the teleport request.");
                            return TravelResult.Failed;
                        }
                    }

                    string body = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode && !body.Contains("\"error\""))
                        return TravelResult.Ok;

                    // A listing that has sold answers 404 "Resource not found"; a token that has
                    // aged out answers 503. Neither is a malfunction, so they're reported apart
                    // from real failures and don't count against the failure tolerance.
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                        response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                        body.Contains("Resource not found") ||
                        body.Contains("no longer available"))
                    {
                        return TravelResult.Expired;
                    }

                    LogError($"BulkBuy: teleport request failed — {(int)response.StatusCode} {response.StatusCode}: {Truncate(body, 200)}");
                    return TravelResult.Failed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError($"BulkBuy: teleport request errored — {ex.Message}");
            return TravelResult.Failed;
        }
    }

    /// <summary>
    /// Waits out the zone load. Purchase-window timing must not start until this returns, or the
    /// timeout burns down while the client is still loading.
    /// </summary>
    private async Task<bool> WaitForLoadingToFinishAsync(CancellationToken ct)
    {
        int interval = Settings.BulkBuy.LoadingCheckInterval?.Value ?? 100;
        int waited = 0;

        while (GameController.IsLoading && waited < 15_000 && _bulkBuyInProgress && !ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            waited += interval;
        }

        return !GameController.IsLoading;
    }

    private async Task<bool> WaitForPurchaseWindowAsync(int timeoutMs, CancellationToken ct)
    {
        _waitingForPurchaseWindow = true;

        try
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

            while (DateTime.Now < deadline && _bulkBuyInProgress && !ct.IsCancellationRequested)
            {
                if (IsPurchaseWindowOpen()) return true;
                await Task.Delay(100, ct);
            }

            return false;
        }
        finally
        {
            _waitingForPurchaseWindow = false;
        }
    }

    private bool IsPurchaseWindowOpen()
    {
        try
        {
            var window = GameController?.IngameState?.IngameUi?.PurchaseWindowHideout;
            return window != null && window.IsVisible;
        }
        catch
        {
            return false;
        }
    }

}

/// <summary>Display model for the item BulkBuy is currently working on.</summary>
public class BulkBuyItem
{
    public string Name { get; set; }
    public string Price { get; set; }
    public string HideoutToken { get; set; }
    public string ItemId { get; set; }
    public string SearchId { get; set; }
    public string AccountName { get; set; }
    public bool IsOnline { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public DateTime AddedTime { get; set; }
    public string Status { get; set; } = "Pending";
}
