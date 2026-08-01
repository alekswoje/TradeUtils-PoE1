using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using TradeUtils.Utility;

namespace TradeUtils;

public partial class TradeUtils
{
    private readonly Queue<LiveSearchInstanceSettings> _connectionQueue = new Queue<LiveSearchInstanceSettings>();
    private DateTime _lastConnectionTime = DateTime.MinValue;
    private string _lastActiveConfigsHash = "";
    private DateTime _lastSettingsChangeTime = DateTime.MinValue;
    private DateTime _lastTickProcessTime = DateTime.MinValue;
    private DateTime _lastAreaChangeTime = DateTime.MinValue;
    private bool _areaChangeCooldownLogged = false;
    private bool _lastEnableState = true;
    private static DateTime _pluginStartTime = DateTime.Now;
    private static DateTime _lastGlobalReset = DateTime.Now;
    
    private bool _lastPurchaseWindowVisible = false;
    private bool _allowMouseMovement = true;
    private bool _windowWasClosedSinceLastMovement = true;
    
    private readonly Queue<Action> _burstQueue = new Queue<Action>();
    private DateTime _lastBurstProcessTime = DateTime.MinValue;
    private int _itemsProcessedThisSecond = 0;
    private DateTime _currentSecond = DateTime.Now;
    private readonly object _burstLock = new object();
    
    partial void InitializeLiveSearch()
    {
        try
        {
            LogMessage("=== TradeUtils LiveSearch Initialization ===");
            
            _rateLimiter = new QuotaGuard(LogMessage, LogError, () => Settings.LiveSearch);
            LogMessage("✓ Rate limiter initialized");
            
            if (_audioDisposalToken == null || _audioDisposalToken.IsCancellationRequested)
            {
                _audioDisposalToken = new CancellationTokenSource();
            }
            _isDisposed = false;
            LogMessage("✓ Audio system initialized");
            
            Settings.LiveSearch.GroupsConfig.PluginInstance = this;
            
            _sessionIdBuffer = Settings.LiveSearch.SecureSessionId ?? "";
            
            if (string.IsNullOrEmpty(_sessionIdBuffer))
            {
                _sessionIdBuffer = Settings.LiveSearch.SessionId.Value ?? "";
                
                if (!string.IsNullOrEmpty(_sessionIdBuffer))
                {
                    LogMessage("⚠️  Using regular session ID (secure storage empty) - migrating to secure storage");
                    Settings.LiveSearch.SecureSessionId = _sessionIdBuffer;
                    LogMessage("✓ Session ID migrated to secure storage");
                }
            }
            
            if (string.IsNullOrEmpty(_sessionIdBuffer))
            {
                LogMessage("❌ ERROR: Session ID is empty. Please set it in the settings.");
                LogMessage("💡 TIP: Go to plugin settings and enter your POESESSID from pathofexile.com cookies");
                LogMessage("📋 HOW TO GET POESESSID:");
                LogMessage("   1. Go to pathofexile.com and log in");
                LogMessage("   2. Press F12 to open Developer Tools");
                LogMessage("   3. Go to Application/Storage → Cookies → pathofexile.com");
                LogMessage("   4. Copy the POESESSID value (32 characters)");
                LogMessage("   5. Paste it in the plugin settings");
            }
            else
            {
                LogMessage($"✓ Session ID loaded (length: {_sessionIdBuffer.Length})");
                
                if (_sessionIdBuffer.Length != 32)
                {
                    LogMessage($"⚠️  WARNING: Session ID length is {_sessionIdBuffer.Length}, expected 32 characters");
                }
                
                if (!_sessionIdBuffer.All(c => char.IsLetterOrDigit(c)))
                {
                    LogMessage("⚠️  WARNING: Session ID contains non-alphanumeric characters");
                }
            }
            
            int totalGroups = Settings.LiveSearch.Groups.Count;
            int enabledGroups = Settings.LiveSearch.Groups.Count(g => g.Enable.Value);
            int totalSearches = Settings.LiveSearch.Groups.Sum(g => g.Searches.Count);
            int enabledSearches = Settings.LiveSearch.Groups
                .Where(g => g.Enable.Value)
                .Sum(g => g.Searches.Count(s => s.Enable.Value));
            
            LogMessage($"📊 Configuration:");
            LogMessage($"   Groups: {enabledGroups}/{totalGroups} enabled");
            LogMessage($"   Searches: {enabledSearches}/{totalSearches} enabled");
            LogMessage($"   Debug Mode: {Settings.LiveSearch.General.DebugMode.Value}");
            LogMessage($"   Show GUI: {true}");
            LogMessage($"   Play Sound: {true}");
            LogMessage($"   Queue Delay: {SearchQueueDelayMs}ms");
            
            if (enabledSearches == 0)
            {
                LogMessage("⚠️  No searches enabled - add and enable searches in settings to start");
            }
            else
            {
                LogMessage($"✓ Ready to start {enabledSearches} search(es)");
            }
            
            LogMessage("=== LiveSearch Initialization Complete ===");
        }
        catch (Exception ex)
        {
            LogError($"❌ Failed to initialize LiveSearch: {ex.Message}");
            LogError($"StackTrace: {ex.StackTrace}");
        }
    }
    
    partial void TickLiveSearch()
    {
        try
        {
            // Check if auto-stash is in progress - pause LiveSearch
            if (_autoStashInProgress)
            {
                if (!_liveSearchPausedForStash)
                {
                    LogMessage("LiveSearch: Auto-stash in progress, pausing...");
                    _liveSearchPausedForStash = true;
                    _liveSearchPaused = true;
                }
                return;
            }
            else if (_liveSearchPausedForStash)
            {
                LogMessage("LiveSearch: Auto-stash completed, resuming...");
                _liveSearchPausedForStash = false;
                _liveSearchPaused = false;
            }

            if (_liveSearchPaused)
            {
                return;
            }
            
            if (!_hasCachedPosition)
            {
                CachePurchaseWindowPosition();
            }
            
            if (_fastModePending)
            {
                var now = DateTime.Now;
                var totalClicks = Math.Max(1, (int)Math.Floor((FastModeClickDurationSec * 1000f) / Math.Max(1, FastModeClickDelayMs)));
                
                LogMessage($"🚀 FAST MODE: Click {_fastModeClickCount + 1}/{totalClicks}, CtrlPressed={_fastModeCtrlPressed}");
                
                if (_fastModeClickCount >= totalClicks)
                {
                    LogMessage("🚀 FAST MODE: All clicks completed, stopping");
                    
                    if (_fastModeCtrlPressed)
                    {
                        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        LogMessage("🚀 FAST MODE: Released Ctrl key");
                        _fastModeCtrlPressed = false;
                    }
                    
                    _fastModePending = false;
                    _fastModeClickCount = 0;
                    _fastModeInInitialPhase = true;
                    _fastModeRetryCount = 0;
                    return;
                }
                
                int currentDelay = FastModeClickDelayMs;
                
                if (_fastModeLastClickTime == DateTime.MinValue || (now - _fastModeLastClickTime).TotalMilliseconds >= currentDelay)
                {
                    try
                    {
                        if (_fastModeClickCount == 0)
                        {
                            LogMessage("🚀 FAST MODE: Starting execution - move cursor, press Ctrl, and first click");
                            
                            if (!_fastModeCtrlPressed)
                            {
                                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                                _fastModeCtrlPressed = true;
                                LogMessage("🚀 FAST MODE: Pressed Ctrl key down");
                                Thread.Sleep(10);
                            }
                            
                            bool executed = TryExecuteFastMode().GetAwaiter().GetResult();
                            if (!executed)
                            {
                                _fastModeRetryCount++;
                                
                                if (_fastModeRetryCount >= 60)
                                {
                                    LogError($"🚀 FAST MODE TIMEOUT: Failed to position cursor after {_fastModeRetryCount} attempts (item likely sold/gone)");
                                    
                                    if (_fastModeCtrlPressed)
                                    {
                                        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                                        LogMessage("🚀 FAST MODE TIMEOUT: Released Ctrl key");
                                    }
                                    
                                    _fastModeCtrlPressed = false;
                                    _fastModePending = false;
                                    _fastModeClickCount = 0;
                                    _fastModeRetryCount = 0;
                                    _fastModeInInitialPhase = true;
                                    
                                    LogMessage("🚀 FAST MODE TIMEOUT: Reset complete, continuing normal operation");
                                }
                                else
                                {
                                    LogMessage($"🚀 FAST MODE: Failed to position cursor, retrying next frame (attempt {_fastModeRetryCount}/60)");
                                    return;
                                }
                            }
                            else
                            {
                                _fastModeRetryCount = 0;
                            }
                        }
                        else
                        {
                            if (!_fastModeCtrlPressed)
                            {
                                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                                _fastModeCtrlPressed = true;
                                LogMessage("🚀 FAST MODE: Re-pressed Ctrl key");
                            }
                        }
                        
                        LogMessage($"🚀 FAST MODE: Click {_fastModeClickCount + 1} (Delay: {currentDelay}ms)");
                        
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                        Thread.Sleep(10);
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                        Thread.Sleep(10);
                        
                        _fastModeClickCount++;
                        _fastModeLastClickTime = now;
                        
                        return;
                    }
                    catch (Exception ex)
                    {
                        LogError($"🚀 FAST MODE ERROR: {ex.Message}");
                    }
                }
                
                return;
            }
            
            if (!Settings.Enable.Value)
            {
                if (_fastModeCtrlPressed)
                {
                    keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    LogMessage("🛑 PLUGIN DISABLED: Released Ctrl key from fast mode");
                    _fastModeCtrlPressed = false;
                    _fastModePending = false;
                    _fastModeClickCount = 0;
                    _fastModeInInitialPhase = true;
                    _fastModeRetryCount = 0;
                }
                
                if (_listeners.Count > 0)
                {
                    LogMessage($"🛑 PLUGIN DISABLED: Stopping {_listeners.Count} active listeners immediately");
                    ForceStopAll();
                }
                return;
            }
            
            if (_emergencyShutdown)
            {
                LogError("🚨 CONNECTION THROTTLING: Global connection limit reached, waiting for cooldown...");
                
                if ((DateTime.Now - _pluginStartTime).TotalMinutes >= 5)
                {
                    LogMessage("♻️  Resetting emergency shutdown state after 5 minutes");
                    _emergencyShutdown = false;
                    _globalConnectionAttempts = 0;
                }
                return;
            }
            
            if (_tpLocked && (DateTime.Now - _tpLockedTime).TotalSeconds >= 10)
            {
                LogMessage("🔓 TP UNLOCKED: 10-second timeout reached in Tick(), unlocking TP");
                _tpLocked = false;
                _tpLockedTime = DateTime.MinValue;
            }
            
            if (_rateLimiter == null)
            {
                _rateLimiter = new QuotaGuard(LogMessage, LogError, () => Settings.LiveSearch);
            }
            
            if (_lastEnableState != Settings.Enable.Value)
            {
                _lastEnableState = Settings.Enable.Value;
                if (!Settings.Enable.Value)
                {
                    LogMessage($"🛑 PLUGIN JUST DISABLED: Force stopping {_listeners.Count} active listeners");
                    ForceStopAll();
                    return;
                }
                else
                {
                    LogMessage("✅ PLUGIN JUST ENABLED: Plugin is now active");
                }
            }
            
            bool recentSettingsChange = (DateTime.Now - _lastSettingsChangeTime).TotalSeconds < 1;
            if ((DateTime.Now - _lastTickProcessTime).TotalSeconds < 2 && !recentSettingsChange)
            {
                if (Settings.Enable.Value)
                {
                    bool hotkeyState = Input.GetKeyState(Settings.LiveSearch.General.TravelHotkey.Value);
                    if (hotkeyState && !_lastHotkeyState)
                    {
                        LogMessage($"🎮 Hotkey {Settings.LiveSearch.General.TravelHotkey.Value} pressed");
                        TravelToHideout(isManual: true);
                    }
                    _lastHotkeyState = hotkeyState;
                }
                return;
            }
            
            bool recentAreaChange = (DateTime.Now - _lastAreaChangeTime).TotalSeconds < 5;
            if (recentAreaChange && !recentSettingsChange)
            {
                if (!_areaChangeCooldownLogged)
                {
                    LogMessage("⏳ AREA CHANGE COOLDOWN: Skipping listener management for 5s after area change");
                    _areaChangeCooldownLogged = true;
                }
                
                bool areaChangePurchaseWindowVisible = GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible;
                
                if (!areaChangePurchaseWindowVisible && _lastPurchaseWindowVisible)
                {
                    LogMessage("🔓 PURCHASE WINDOW CLOSED (During Area Cooldown): Mouse movement will be allowed on next window open");
                    _windowWasClosedSinceLastMovement = true;
                    _allowMouseMovement = true;
                }
                
                if (areaChangePurchaseWindowVisible && !_lastPurchaseWindowVisible)
                {
                    LogMessage($"🔔 PURCHASE WINDOW OPENED (During Area Cooldown): MoveMouseToItem={true}, TeleportedLocation={(_teleportedItemLocation.X != 0 || _teleportedItemLocation.Y != 0 ? $"({_teleportedItemLocation.X}, {_teleportedItemLocation.Y})" : "null")}");
                    
                    if (_tpLocked)
                            {
                                LogMessage("🔓 TP UNLOCKED (During Area Cooldown): Purchase window opened successfully");
                                _tpLocked = false;
                                _tpLockedTime = DateTime.MinValue;
                            }
                }
                
                if (areaChangePurchaseWindowVisible && !_lastPurchaseWindowVisible &&
                    true)
                {
                    bool hasTeleportedItemLocation = _teleportedItemLocation.X != 0 || _teleportedItemLocation.Y != 0;
                    if (_allowMouseMovement && (_windowWasClosedSinceLastMovement || hasTeleportedItemLocation))
                    {
                        if (_teleportedItemLocation.X != 0 || _teleportedItemLocation.Y != 0)
                        {
                            LogMessage($"🖱️ SAFE MOUSE MOVE (During Area Cooldown): Moving to teleported item at ({_teleportedItemLocation.X}, {_teleportedItemLocation.Y})");
                            MoveMouseToItemLocation(_teleportedItemLocation.X, _teleportedItemLocation.Y);
                            _teleportedItemLocation = (0, 0);
                            _allowMouseMovement = false;
                            _windowWasClosedSinceLastMovement = false;
                        }
                        else if (_recentItems.Count > 0)
                        {
                            lock (_recentItemsLock)
                            {
                                if (_recentItems.Count > 0)
                                {
                                    LogMessage("🖱️ SAFE FALLBACK MOVE (During Area Cooldown): Using most recent item");
                                    var item = _recentItems.Peek();
                                    MoveMouseToItemLocation(item.X, item.Y);
                                    _allowMouseMovement = false;
                                    _windowWasClosedSinceLastMovement = false;
                                }
                            }
                        }
                    }
                }
                
                _lastPurchaseWindowVisible = areaChangePurchaseWindowVisible;
                
                return;
            }
            else if (!recentAreaChange)
            {
                _areaChangeCooldownLogged = false;
            }
            
            _lastTickProcessTime = DateTime.Now;
            
            if (!Settings.Enable.Value && _listeners.Count > 0)
            {
                LogMessage($"🛑 SAFETY CHECK: Plugin disabled but {_listeners.Count} listeners still active - forcing stop");
                ForceStopAll();
                return;
            }
            
            if (_liveSearchStarted)
            {
                ProcessConnectionQueue();
            }
            ProcessBurstQueue();
            
            if ((DateTime.Now - _lastGlobalReset).TotalMinutes >= 2)
            {
                if (_globalConnectionAttempts > 0)
                {
                    LogMessage($"♻️  PERIODIC RESET: Clearing global connection attempts ({_globalConnectionAttempts} -> 0) after 2 minutes");
                    _globalConnectionAttempts = 0;
                    _lastGlobalReset = DateTime.Now;
                }
            }
            
            bool currentHotkeyState = Input.GetKeyState(Settings.LiveSearch.General.TravelHotkey.Value);
            if (currentHotkeyState && !_lastHotkeyState)
            {
                LogMessage($"🎮 MANUAL HOTKEY PRESSED: {Settings.LiveSearch.General.TravelHotkey.Value} - initiating manual teleport");
                TravelToHideout(isManual: true);
            }
            _lastHotkeyState = currentHotkeyState;
            
            if (Settings.LiveSearch.AutoFeatures.AutoStash.Value && !_autoStashInProgress && !_autoTpPaused)
            {
                if (!GameController.IsLoading && GameController.InGame && (_rateLimiter == null || !_rateLimiter.IsRateLimited()))
                {
                    if (IsInventoryFullFor2x4Item())
                    {
                        LogMessage("📦 AUTO STASH: Inventory is full, starting auto stash routine...");
                        _ = ExecuteAutoStashRoutine();
                    }
                }
            }
            
            bool currentPurchaseWindowVisible = GameController.IngameState.IngameUi.PurchaseWindowHideout.IsVisible;
            
            if (!currentPurchaseWindowVisible && _lastPurchaseWindowVisible)
            {
                LogMessage("🔓 PURCHASE WINDOW CLOSED: Mouse movement will be allowed on next window open");
                _windowWasClosedSinceLastMovement = true;
                _allowMouseMovement = true;
            }
            
            if (currentPurchaseWindowVisible && !_lastPurchaseWindowVisible)
            {
                LogMessage($"🔔 PURCHASE WINDOW OPENED: MoveMouseToItem={true}, ForceAutoBuy={_forceAutoBuy}, TeleportedLocation={(_teleportedItemLocation.X != 0 || _teleportedItemLocation.Y != 0 ? $"({_teleportedItemLocation.X}, {_teleportedItemLocation.Y})" : "null")}, AllowMovement={_allowMouseMovement}");
                
                // Unlock TP when purchase window opens
                if (_tpLocked)
                {
                    LogMessage("🔓 TP UNLOCKED: Purchase window opened successfully");
                    _tpLocked = false;
                    _tpLockedTime = DateTime.MinValue;
                }
            }
            
            // Move mouse to item when window opens (if enabled and we have a location)
            if (currentPurchaseWindowVisible && !_lastPurchaseWindowVisible &&
                true)
            {
                if (!_allowMouseMovement)
                {
                    LogMessage("⚠️ MOUSE MOVE BLOCKED: _allowMouseMovement is false (previous movement not completed)");
                }
                else
                {
                    bool hasTeleportedItemLocation = _teleportedItemLocation.X != 0 || _teleportedItemLocation.Y != 0;
                    if (!_windowWasClosedSinceLastMovement && !hasTeleportedItemLocation)
                    {
                        LogMessage("⚠️ MOUSE MOVE BLOCKED: Window was not closed since last movement (preventing accidental purchases)");
                    }
                    else if (_allowMouseMovement && (_windowWasClosedSinceLastMovement || hasTeleportedItemLocation))
                {
                    if (_teleportedItemLocation.X != 0 || _teleportedItemLocation.Y != 0)
                    {
                        LogMessage($"🖱️ SAFE MOUSE MOVE: Window was closed, moving to teleported item at ({_teleportedItemLocation.X}, {_teleportedItemLocation.Y})");
                        MoveMouseToItemLocation(_teleportedItemLocation.X, _teleportedItemLocation.Y);
                        _teleportedItemLocation = (0, 0); // Clear after use
                        _allowMouseMovement = false; // Block further movement until window closes
                        _windowWasClosedSinceLastMovement = false;
                    }
                    else if (_recentItems.Count > 0)
                    {
                        lock (_recentItemsLock)
                        {
                            if (_recentItems.Count > 0)
                            {
                                LogMessage("🖱️ SAFE FALLBACK MOVE: Window was closed, using most recent item");
                                var item = _recentItems.Peek();
                                MoveMouseToItemLocation(item.X, item.Y);
                                _allowMouseMovement = false;
                                _windowWasClosedSinceLastMovement = false;
                            }
                        }
                    }
                    else
                    {
                        LogMessage("⚠️ NO ITEMS: No teleported item location or recent items available for mouse movement");
                    }
                }
                }
            }
            
            _lastPurchaseWindowVisible = currentPurchaseWindowVisible;
            
            // Get all enabled search configs
            var allConfigs = Settings.LiveSearch.Groups
                .Where(g => g.Enable.Value)
                .SelectMany(g => g.Searches.Where(s => s.Enable.Value))
                .ToList();
            
            if (allConfigs.Count > 30)
            {
                LogError("❌ Exceeded max 30 searches; limiting to first 30");
                allConfigs = allConfigs.Take(30).ToList();
            }
            
            // Create a set of active config identifiers for comparison
            var activeConfigIds = allConfigs
                .Select(c => $"{c.League.Value}|{c.SearchId.Value}")
                .ToHashSet();
            
            // Check if the active configs have changed (for immediate response to settings changes)
            var currentConfigsHash = string.Join("|", activeConfigIds.OrderBy(x => x));
            bool settingsChanged = _lastActiveConfigsHash != currentConfigsHash;
            if (settingsChanged)
            {
                _lastActiveConfigsHash = currentConfigsHash;
                _lastSettingsChangeTime = DateTime.Now;
                LogMessage($"⚙️  SETTINGS CHANGED: Config changed from {_listeners.Count} to {allConfigs.Count} searches - processing immediately");
                
                // Reset global attempts for user actions to allow fresh start
                if (_globalConnectionAttempts > 0)
                {
                    LogMessage($"♻️  SETTINGS RESET: Clearing global connection attempts ({_globalConnectionAttempts} -> 0) for fresh start");
                    _globalConnectionAttempts = 0;
                }
                
                // Reset emergency shutdown on settings change (user is actively configuring)
                if (_emergencyShutdown)
                {
                    LogMessage("♻️  EMERGENCY RESET: Settings changed - clearing emergency shutdown state");
                    _emergencyShutdown = false;
                }
            }
            
            // CRITICAL: Check for disabled listeners efficiently
            if (!settingsChanged)
            {
                StopDisabledListeners();
            }
            else
            {
                // When settings changed, do immediate cleanup
                LogMessage($"🧹 IMMEDIATE CLEANUP: Processing settings change for {allConfigs.Count} target configs");
                StopDisabledListeners();
                
                // Clean up any existing duplicates immediately
                var duplicateGroups = _listeners.GroupBy(l => $"{l.Config.League.Value}|{l.Config.SearchId.Value}")
                    .Where(g => g.Count() > 1).ToList();
                
                foreach (var group in duplicateGroups)
                {
                    var duplicates = group.Skip(1).ToList();
                    LogMessage($"🧹 SETTINGS CLEANUP: Removing {duplicates.Count} duplicates for {group.Key}");
                    foreach (var duplicate in duplicates)
                    {
                        duplicate.Stop();
                        _listeners.Remove(duplicate);
                    }
                }
            }
            
            // CRITICAL: Stop listeners for disabled searches
            foreach (var listener in _listeners.ToList())
            {
                var listenerId = $"{listener.Config.League.Value}|{listener.Config.SearchId.Value}";
                
                bool searchStillActive = false;
                bool foundMatchingConfig = false;
                string disableReason = "";
                
                foreach (var group in Settings.LiveSearch.Groups)
                {
                    if (!group.Enable.Value) continue;
                    
                    foreach (var search in group.Searches)
                    {
                        if (search.League.Value == listener.Config.League.Value &&
                            search.SearchId.Value == listener.Config.SearchId.Value)
                        {
                            foundMatchingConfig = true;
                            
                            if (!group.Enable.Value)
                            {
                                disableReason = $"group '{group.Name.Value}' disabled";
                                searchStillActive = false;
                            }
                            else if (!search.Enable.Value)
                            {
                                disableReason = "search disabled";
                                searchStillActive = false;
                            }
                            else
                            {
                                searchStillActive = true;
                            }
                            break;
                        }
                    }
                    if (foundMatchingConfig) break;
                }
                
                // If we didn't find the config at all, or it's disabled, stop the listener
                if (!foundMatchingConfig || !searchStillActive)
                {
                    if (!foundMatchingConfig)
                    {
                        disableReason = "config not found";
                    }
                    LogMessage($"🛑 DISABLING: {listener.Config.SearchId.Value} - {disableReason}");
                    listener.Stop();
                    _listeners.Remove(listener);
                }
                else if (!listener.IsRunning && !listener.IsConnecting)
                {
                    // Check global throttling with exponential backoff
                    if (_globalConnectionAttempts >= 3)
                    {
                        int globalDelay = CalculateExponentialBackoffDelay(_globalConnectionAttempts);
                        LogMessage($"⏳ GLOBAL THROTTLE: Skipping restart due to global attempts ({_globalConnectionAttempts}), using exponential backoff ({globalDelay/1000}s)");
                        continue;
                    }
                    
                    // Multiple safety checks before attempting restart
                    if ((DateTime.Now - listener.LastErrorTime).TotalSeconds < Settings.LiveSearch.RestartCooldownSeconds)
                    {
                        LogDebug($"⏱️  RESTART BLOCKED: Search {listener.Config.SearchId.Value} in error cooldown");
                        continue;
                    }
                    
                    // Exponential backoff: Calculate delay based on attempt count
                    if (listener.ConnectionAttempts > 0)
                    {
                        int delayMs = CalculateExponentialBackoffDelay(listener.ConnectionAttempts);
                        
                        if ((DateTime.Now - listener.LastConnectionAttempt).TotalMilliseconds < delayMs)
                        {
                            int remainingMs = delayMs - (int)(DateTime.Now - listener.LastConnectionAttempt).TotalMilliseconds;
                            LogDebug($"⏳ EXPONENTIAL BACKOFF: Search {listener.Config.SearchId.Value} waiting {remainingMs/1000}s (attempt #{listener.ConnectionAttempts})");
                            continue;
                        }
                    }
                    
                    LogMessage($"🔄 ATTEMPTING RESTART: Search {listener.Config.SearchId.Value}");
                    listener.Start(LogMessage, LogError, Settings.LiveSearch.SecureSessionId);
                }
            }
            
            // Start new listeners for enabled searches - add to queue instead of immediate start
            foreach (var config in allConfigs)
            {
                var configId = $"{config.League.Value}|{config.SearchId.Value}";
                
                // CRITICAL: Comprehensive duplicate detection
                var existingListeners = _listeners.Where(l =>
                    l.Config.League.Value == config.League.Value &&
                    l.Config.SearchId.Value == config.SearchId.Value).ToList();
                
                if (existingListeners.Count > 1)
                {
                    // REMOVE EXTRA DUPLICATES
                    LogMessage($"🧹 DUPLICATE CLEANUP: Found {existingListeners.Count} listeners for {config.SearchId.Value}, removing {existingListeners.Count - 1} extras");
                    for (int i = 1; i < existingListeners.Count; i++)
                    {
                        existingListeners[i].Stop();
                        _listeners.Remove(existingListeners[i]);
                    }
                }
                
                var existingListener = existingListeners.FirstOrDefault();
                
                if (existingListener == null)
                {
                    // FINAL CHECK: Ensure absolutely no duplicates
                    var finalDuplicateCheck = _listeners.Any(l =>
                        l.Config.League.Value == config.League.Value &&
                        l.Config.SearchId.Value == config.SearchId.Value);
                    
                    if (finalDuplicateCheck)
                    {
                        LogDebug($"⚠️  FINAL DUPLICATE PREVENTION: Listener already exists for {config.SearchId.Value}");
                        continue;
                    }
                    
                    // EMERGENCY: Use exponential backoff for new listener creation
                    if (_globalConnectionAttempts >= 5 && !recentSettingsChange)
                    {
                        int emergencyDelay = CalculateExponentialBackoffDelay(_globalConnectionAttempts);
                        LogMessage($"🚨 EMERGENCY BACKOFF: Preventing new listener creation - global attempts ({_globalConnectionAttempts}), using {emergencyDelay/1000}s delay");
                        continue;
                    }
                    else if (recentSettingsChange && _globalConnectionAttempts >= 1)
                    {
                        // Reset global attempts for user-initiated changes (more forgiving)
                        LogMessage($"♻️  USER ACTION: Resetting global connection attempts ({_globalConnectionAttempts} -> 0) for settings change");
                        _globalConnectionAttempts = 0;
                    }
                    
                    // Add to connection queue instead of immediate start
                    if (!_connectionQueue.Any(q => q.League.Value == config.League.Value &&
                                                  q.SearchId.Value == config.SearchId.Value))
                    {
                        _connectionQueue.Enqueue(config);
                        LogMessage($"📥 QUEUED: Search {config.SearchId.Value} added to connection queue (Position: {_connectionQueue.Count})");
                    }
                }
                else
                {
                    // Log state changes
                    if (existingListener.IsRunning != existingListener.LastIsRunning ||
                        existingListener.IsConnecting != existingListener.LastIsConnecting)
                    {
                        LogDebug($"ℹ️  EXISTING LISTENER: {config.SearchId.Value} (Running: {existingListener.IsRunning}, Connecting: {existingListener.IsConnecting})");
                        
                        existingListener.LastIsRunning = existingListener.IsRunning;
                        existingListener.LastIsConnecting = existingListener.IsConnecting;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"❌ Error in LiveSearch tick: {ex.Message}");
            LogError($"StackTrace: {ex.StackTrace}");
        }
    }
    
    /// <summary>
    /// Process connection queue method - rate-limited listener creation
    /// </summary>
    private void ProcessConnectionQueue()
    {
        if (_connectionQueue.Count == 0) return;
        
        // Check if enough time has passed since last connection
        var timeSinceLastConnection = (DateTime.Now - _lastConnectionTime).TotalMilliseconds;
        if (timeSinceLastConnection < SearchQueueDelayMs)
        {
            LogDebug($"⏳ DEBUG: Search queue delay active - {SearchQueueDelayMs - timeSinceLastConnection:F0}ms remaining");
            return;
        }
        
        var config = _connectionQueue.Dequeue();
        _lastConnectionTime = DateTime.Now;
        
        // Final duplicate check
        if (_listeners.Any(l => l.Config.League.Value == config.League.Value &&
                               l.Config.SearchId.Value == config.SearchId.Value))
        {
            LogMessage($"⚠️  QUEUE SKIP: Listener already exists for {config.SearchId.Value}");
            return;
        }
        
        var newListener = new SearchListener(this, config, LogMessage, LogError);
        _listeners.Add(newListener);
        LogMessage($"🚀 STARTING FROM QUEUE: Search {config.SearchId.Value}");
        
        LogDebug($"🔍 DEBUG: Search queue delay was {SearchQueueDelayMs}ms, time since last connection: {timeSinceLastConnection:F0}ms");
        
        newListener.Start(LogMessage, LogError, Settings.LiveSearch.SecureSessionId);
    }
    
    /// <summary>
    /// Process burst queue for rate-limited item processing
    /// </summary>
    private void ProcessBurstQueue()
    {
        lock (_burstLock)
        {
            if (_burstQueue.Count == 0)
            {
                // Only log occasionally to avoid spam
                if ((DateTime.Now - _lastBurstProcessTime).TotalSeconds >= 10)
                {
                    LogDebug($"📊 BURST QUEUE STATUS: Empty (nothing to process)");
                    _lastBurstProcessTime = DateTime.Now;
                }
                return;
            }
            
            LogMessage($"🔄 PROCESSING BURST QUEUE: {_burstQueue.Count} actions queued");
            
            // Reset counter if we're in a new second
            if ((DateTime.Now - _currentSecond).TotalSeconds >= 1)
            {
                if (_itemsProcessedThisSecond > 0)
                {
                    LogDebug($"📊 RATE LIMIT RESET: Processed {_itemsProcessedThisSecond} items in the last second");
                }
                _itemsProcessedThisSecond = 0;
                _currentSecond = DateTime.Now;
            }
            
            // Check if we can process more items this second
            int maxItemsPerSecond = MaxItemsPerSecond;
            if (_itemsProcessedThisSecond >= maxItemsPerSecond)
            {
                LogMessage($"⏳ BURST PROTECTION: Already processed {_itemsProcessedThisSecond}/{maxItemsPerSecond} items this second - waiting");
                return;
            }
            
            // Process as many items as allowed
            int processedThisTick = 0;
            while (_burstQueue.Count > 0 && _itemsProcessedThisSecond < maxItemsPerSecond)
            {
                var action = _burstQueue.Dequeue();
                LogMessage($"▶️ EXECUTING BURST ACTION: {_itemsProcessedThisSecond + 1}/{maxItemsPerSecond} this second");
                action?.Invoke();
                _itemsProcessedThisSecond++;
                processedThisTick++;
            }
            
            LogMessage($"✅ PROCESSED {processedThisTick} actions from burst queue");
            
            if (_burstQueue.Count > 0)
            {
                LogMessage($"📊 BURST QUEUE: {_burstQueue.Count} actions still waiting");
            }
            else
            {
                LogMessage($"✨ BURST QUEUE: Empty - all actions processed");
            }
        }
    }
    
    /// <summary>
    /// Calculates exponential backoff delay based on attempt count
    /// </summary>
    private int CalculateExponentialBackoffDelay(int attemptCount)
    {
        if (attemptCount <= 0) return 0;
        
        // Exponential backoff with reasonable limits:
        // Attempt 1: 1s, 2: 2s, 3: 4s, 4: 8s, 5: 16s, 6: 32s, 7: 60s, 8: 120s, 9: 300s, 10: 600s, 11+: 1800s (30min)
        int[] delays = { 1000, 2000, 4000, 8000, 16000, 32000, 60000, 120000, 300000, 600000, 1800000 };
        
        int index = Math.Min(attemptCount - 1, delays.Length - 1);
        return delays[index];
    }
    
    /// <summary>
    /// Stop disabled listeners
    /// </summary>
    private void StopDisabledListeners()
    {
        var listenersToRemove = new List<SearchListener>();
        
        foreach (var listener in _listeners.ToList())
        {
            // Check if the search itself is disabled OR its parent group is disabled
            bool searchStillActive = false;
            bool foundMatchingConfig = false;
            string disableReason = "";
            
            foreach (var group in Settings.LiveSearch.Groups)
            {
                foreach (var search in group.Searches)
                {
                    if (search.League.Value == listener.Config.League.Value &&
                        search.SearchId.Value == listener.Config.SearchId.Value)
                    {
                        foundMatchingConfig = true;
                        
                        // CRITICAL: Group disable overrides everything
                        if (!group.Enable.Value)
                        {
                            disableReason = $"group '{group.Name.Value}' disabled";
                            searchStillActive = false;
                        }
                        else if (!search.Enable.Value)
                        {
                            disableReason = "search disabled";
                            searchStillActive = false;
                        }
                        else
                        {
                            searchStillActive = true;
                        }
                        break;
                    }
                }
                if (foundMatchingConfig) break;
            }
            
            // If we didn't find the config at all, or it's disabled, stop the listener
            if (!foundMatchingConfig || !searchStillActive)
            {
                if (!foundMatchingConfig)
                {
                    disableReason = "config not found";
                }
                LogMessage($"🛑 STOPPING DISABLED: {listener.Config.SearchId.Value} - {disableReason}");
                listener.Stop();
                listenersToRemove.Add(listener);
            }
        }
        
        foreach (var listener in listenersToRemove)
        {
            _listeners.Remove(listener);
        }
    }
    
    partial void AreaChangeLiveSearch(AreaInstance area)
    {
        try
        {
            _lastAreaChangeTime = DateTime.Now;
            LogDebug($"🗺️  LiveSearch: Area changed to {area.DisplayName}");
            
            // Reset teleport state on area change
            _isManualTeleport = false;
            _currentTeleportingItem = null;
            _tpLocked = false;
            
            LogDebug("🔄 LiveSearch: Teleport state reset");
            
            if (_teleportedItemLocation.X != 0 && _teleportedItemLocation.Y != 0)
            {
                var coords = _teleportedItemLocation;
                _teleportedItemLocation = (0, 0);
                
                string searchId = null;
                if (_currentTeleportingItem != null && !string.IsNullOrEmpty(_currentTeleportingItem.SearchId))
                {
                    searchId = _currentTeleportingItem.SearchId;
                }
                
                var searchConfig = !string.IsNullOrEmpty(searchId) ? GetSearchConfigBySearchId(searchId) : null;
                bool fastModeEnabled = searchConfig?.FastMode.Value ?? Settings.LiveSearch.AutoFeatures.FastMode.Value;
                
                if (fastModeEnabled)
                {
                    LogMessage($"🚀 FAST MODE: Area loaded, initiating fast mode for coordinates ({coords.X}, {coords.Y})");
                    
                    _fastModePending = true;
                    _fastModeCoords = coords;
                    _fastModeStartTime = DateTime.Now;
                    _fastModeClickCount = 0;
                    _fastModeCtrlPressed = false;
                    _fastModeInInitialPhase = true;
                    _fastModeLastClickTime = DateTime.MinValue;
                    _fastModeRetryCount = 0;
                    
                    LogMessage($"🚀 FAST MODE INIT: duration={FastModeClickDurationSec}s, delay={FastModeClickDelayMs}ms");
                    
                    var totalClicks = Math.Max(1, (int)Math.Floor((FastModeClickDurationSec * 1000f) / Math.Max(1, FastModeClickDelayMs)));
                    LogMessage($"🚀 FAST MODE: Will perform approximately {totalClicks} clicks");
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"❌ Error in LiveSearch area change: {ex.Message}");
        }
    }
    
    partial void DisposeLiveSearch()
    {
        try
        {
            LogMessage("🧹 LiveSearch: Disposing...");
            
            // Stop all listeners
            ForceStopAll();
            
            // Clear listeners
            _listeners?.Clear();
            
            // Dispose rate limiter
            _rateLimiter = null;
            
            // Mark as disposed
            _isDisposed = true;
            _audioDisposalToken?.Cancel();
            
            LogMessage("✓ LiveSearch: Disposed successfully");
        }
        catch (Exception ex)
        {
            LogError($"❌ Error disposing LiveSearch: {ex.Message}");
        }
    }
    
    partial void OpenAllEnabledSearchesInBrowserInternal()
    {
        try
        {
            if (Settings?.LiveSearch.Groups == null || Settings.LiveSearch.Groups.Count == 0)
            {
                LogMessage("⚠️  No search groups configured");
                return;
            }
            
            var enabledSearches = new List<(string searchId, string league, string name)>();
            
            foreach (var group in Settings.LiveSearch.Groups)
            {
                if (!group.Enable.Value) continue;
                
                foreach (var search in group.Searches)
                {
                    if (search.Enable.Value && !string.IsNullOrWhiteSpace(search.SearchId.Value))
                    {
                        enabledSearches.Add((search.SearchId.Value, search.League.Value, search.Name.Value));
                    }
                }
            }
            
            if (enabledSearches.Count == 0)
            {
                LogMessage("⚠️  No enabled searches found");
                return;
            }
            
            LogMessage($"🌐 Opening {enabledSearches.Count} enabled searches in browser...");
            
            foreach (var (searchId, league, name) in enabledSearches)
            {
                LogMessage($"  ↗️  Opening: {name} ({searchId})");
                var finalLeague = string.IsNullOrWhiteSpace(league) ? "Standard" : league;
                string searchUrl = $"https://www.pathofexile.com/trade/search/{Uri.EscapeDataString(finalLeague)}/{searchId}";
                
                System.Diagnostics.Process.Start("cmd", $"/c start {searchUrl}");
                
                // Add configurable delay between opening tabs
                int delayMs = BrowserTabDelaySeconds * 1000;
                System.Threading.Thread.Sleep(delayMs);
            }
            
            LogMessage($"✓ Successfully opened {enabledSearches.Count} searches in browser");
        }
        catch (Exception ex)
        {
            LogError($"❌ Failed to open enabled searches: {ex.Message}");
        }
    }
}










