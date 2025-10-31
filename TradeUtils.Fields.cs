using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Numerics;
using System.Threading;
using TradeUtils.Models;
using TradeUtils.Utility;

namespace TradeUtils;

public partial class TradeUtils
{
    // ==================== PRIVATE FIELDS ====================
    
    // Listeners and search management
    private List<SearchListener> _listeners = new List<SearchListener>();
    private SearchListener _activeListener;
    
    // HTTP client for API requests
    private static readonly HttpClient _httpClient = new HttpClient();
    
    // Recent items tracking
    private Queue<RecentItem> _recentItems = new Queue<RecentItem>();
    private readonly object _recentItemsLock = new object();
    
    // Rate limiting
    private QuotaGuard _rateLimiter;
    
    // Teleport/Travel state
    private bool _isManualTeleport = false;
    private RecentItem _currentTeleportingItem;
    private RecentItem _teleportedItemInfo;
    private (int X, int Y) _teleportedItemLocation;
    private bool _tpLocked = false;
    private DateTime _tpLockedTime = DateTime.MinValue;
    private DateTime _lastTpTime = DateTime.MinValue;
    private bool _autoTpPaused = false;
    
    // Audio playback
    private bool _playSound = true;
    private bool _isDisposed = false;
    private CancellationTokenSource _audioDisposalToken = new CancellationTokenSource();
    private System.Threading.SemaphoreSlim _audioSemaphore = new System.Threading.SemaphoreSlim(1, 1);
    
    // Auto-stash
    private bool _autoStashInProgress = false;
    private DateTime _autoStashStartTime = DateTime.MinValue;
    
    // Settings UI state
    private string _sessionIdBuffer = "";
    private bool _settingsUpdated = false;
    
    // Random number generator
    private static readonly Random _random = new Random();
    
    // Emergency shutdown tracking
    private bool _emergencyShutdown = false;
    private int _globalConnectionAttempts = 0;
    
    // ==================== HELPER METHODS ====================
    
    /// <summary>
    /// Log debug messages (only if debug mode is enabled)
    /// </summary>
    protected void LogDebug(string message)
    {
        if (LiveSearchSettings.General.DebugMode?.Value == true)
        {
            LogMessage($"[DEBUG] {message}", 2);
        }
    }
    
    /// <summary>
    /// Log informational messages
    /// </summary>
    protected void LogInfo(string message)
    {
        LogMessage($"[INFO] {message}", 5);
    }
    
    /// <summary>
    /// Log warning messages
    /// </summary>
    protected void LogWarning(string message)
    {
        LogMessage($"[WARNING] {message}", 10);
    }
    
    /// <summary>
    /// Get plugin temporary directory
    /// </summary>
    protected string GetPluginTempDirectory()
    {
        var tempDir = System.IO.Path.Combine(DirectoryFullName, "temp");
        if (!System.IO.Directory.Exists(tempDir))
        {
            System.IO.Directory.CreateDirectory(tempDir);
        }
        return tempDir;
    }
    
    /// <summary>
    /// Test temporary directory access
    /// </summary>
    protected void TestTempDirectory()
    {
        try
        {
            var tempDir = GetPluginTempDirectory();
            var testFile = System.IO.Path.Combine(tempDir, "test.txt");
            System.IO.File.WriteAllText(testFile, "test");
            System.IO.File.Delete(testFile);
            LogMessage("Temp directory test passed");
        }
        catch (Exception ex)
        {
            LogError($"Temp directory test failed: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Force stop all search listeners
    /// </summary>
    protected void ForceStopAll()
    {
        foreach (var listener in _listeners)
        {
            try
            {
                listener?.Cts?.Cancel();
                listener.IsRunning = false;
                listener.IsConnecting = false;
            }
            catch (Exception ex)
            {
                LogError($"Error stopping listener: {ex.Message}");
            }
        }
        LogMessage("All searches force stopped");
    }
    
    /// <summary>
    /// Check if a key is currently pressed
    /// </summary>
    protected bool IsKeyPressed(System.Windows.Forms.Keys key)
    {
        if (key == System.Windows.Forms.Keys.None) return false;
        // Use Windows API to check key state
        return (GetAsyncKeyState((int)key) & 0x8000) != 0;
    }
    
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    
    /// <summary>
    /// Log search result to file (overload for RecentItem)
    /// </summary>
    protected void LogSearchResult(RecentItem item)
    {
        if (!LiveSearchSettings.SearchSettings.LogSearchResults.Value) return;
        
        try
        {
            var logFile = System.IO.Path.Combine(DirectoryFullName, "search_results.csv");
            var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{item.Name},{item.Price}\n";
            System.IO.File.AppendAllText(logFile, logEntry);
        }
        catch (Exception ex)
        {
            LogError($"Failed to log search result: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Log search result to file (overload for ResultItem)
    /// </summary>
    protected void LogSearchResult(Models.ResultItem item)
    {
        if (!LiveSearchSettings.SearchSettings.LogSearchResults.Value) return;
        
        try
        {
            var logFile = System.IO.Path.Combine(DirectoryFullName, "search_results.csv");
            var itemName = item.Item?.Name ?? item.Item?.TypeLine ?? "Unknown";
            var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss},{itemName}\n";
            System.IO.File.AppendAllText(logFile, logEntry);
        }
        catch (Exception ex)
        {
            LogError($"Failed to log search result: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Update auto-buy attempt tracking
    /// </summary>
    protected void UpdateAutoBuyAttempt(string itemName, string searchId)
    {
        // Track auto-buy attempts for statistics
        LogDebug($"Auto-buy attempt for {itemName} (Search: {searchId})");
    }
    
    /// <summary>
    /// Queue items for processing (placeholder for future implementation)
    /// </summary>
    protected void QueueItemsForProcessing(string[] itemIds, Action<string> logMessage, Action<string> logError, string sessionId, object listener)
    {
        LogDebug($"Queued {itemIds.Length} items for processing");
        // For now, just log them - full implementation would involve rate-limited processing
    }
    
    /// <summary>
    /// Test LiveSearch functionality
    /// </summary>
    protected void TestLiveSearchFunctionality()
    {
        LogMessage("Testing LiveSearch functionality...");
        LogMessage($"Session ID configured: {!string.IsNullOrEmpty(LiveSearchSettings.SecureSessionId)}");
        LogMessage($"Active listeners: {_listeners.Count}");
        LogMessage("Test complete");
    }
    
    // ==================== WINDOWS API IMPORTS ====================
    
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);
    
    // Virtual key codes
    private const byte VK_CONTROL = 0x11;
    
    // Key event flags
    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    
    // Mouse event flags  
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
}

