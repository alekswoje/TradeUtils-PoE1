using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using ExileCore.Shared.Nodes;

namespace TradeUtils;

public partial class TradeUtils
{
    private void RenderLiveSearchGui()
    {
        // Always show GUI when LiveSearch is enabled
        if (!Settings.LiveSearch.Enable.Value) return;

        try
        {
            // Set window position
            ImGui.SetNextWindowPos(Settings.LiveSearch.WindowPosition, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(500, 400), ImGuiCond.FirstUseEver);

            bool showGui = true; // Always show when enabled
            if (ImGui.Begin("TradeUtils - Live Search", ref showGui, 
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
            {
                // If window was closed (showGui became false), disable LiveSearch
                if (!showGui)
                {
                    Settings.LiveSearch.Enable.Value = false;
                    ImGui.End();
                    return;
                }
                
                // Save window position
                Settings.LiveSearch.WindowPosition = ImGui.GetWindowPos();

                // Close button in top right
                float windowWidth = ImGui.GetWindowWidth();
                float buttonWidth = 80;
                ImGui.SetCursorPosX(windowWidth - buttonWidth - ImGui.GetStyle().WindowPadding.X);
                ImGui.SetCursorPosY(ImGui.GetStyle().WindowPadding.Y);
                
                if (ImGui.Button("Close", new Vector2(buttonWidth, 0)))
                {
                    // Closing the GUI disables the LiveSearch sub-plugin
                    Settings.LiveSearch.Enable.Value = false;
                }

                ImGui.Spacing();
                ImGui.Spacing();

                // Title and status
                ImGui.TextColored(new Vector4(0.3f, 0.7f, 1.0f, 1.0f), "=== Live Search ===");
                ImGui.Spacing();

                // Status display
                int activeListeners = _listeners.Count(l => l.IsRunning);
                int totalListeners = _listeners.Count;
                
                if (_liveSearchPaused)
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.0f, 1.0f), "PAUSED");
                }
                else if (activeListeners > 0)
                {
                    ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "RUNNING");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "STOPPED");
                }
                
                ImGui.SameLine();
                ImGui.Text($"Listeners: {activeListeners}/{totalListeners}");

                ImGui.Separator();
                ImGui.Spacing();

                // Control buttons
                if (activeListeners == 0 && !_liveSearchPaused)
                {
                    if (ImGui.Button("Start", new Vector2(100, 30)))
                    {
                        // Start all enabled searches
                        StartLiveSearch();
                    }
                }
                else if (_liveSearchPaused)
                {
                    if (ImGui.Button("Resume", new Vector2(100, 30)))
                    {
                        _liveSearchPaused = false;
                        LogMessage("LiveSearch: Resumed");
                    }
                    
                    ImGui.SameLine();
                    
                    if (ImGui.Button("Stop", new Vector2(100, 30)))
                    {
                        StopLiveSearch();
                    }
                }
                else
                {
                    if (ImGui.Button("Pause", new Vector2(100, 30)))
                    {
                        _liveSearchPaused = true;
                        LogMessage("LiveSearch: Paused (websockets remain open)");
                    }
                    
                    ImGui.SameLine();
                    
                    if (ImGui.Button("Stop", new Vector2(100, 30)))
                    {
                        StopLiveSearch();
                    }
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                // Statistics
                ImGui.Text("Statistics:");
                ImGui.Indent();
                
                int recentItemsCount;
                lock (_recentItemsLock)
                {
                    recentItemsCount = _recentItems.Count;
                }
                
                ImGui.Text($"Items in Queue: {recentItemsCount}");
                ImGui.Text($"Processed: {Settings.LiveSearch.TotalItemsProcessed}");
                ImGui.Text($"Successful: {Settings.LiveSearch.SuccessfulPurchases}");
                ImGui.Text($"Failed: {Settings.LiveSearch.FailedPurchases}");
                
                if (Settings.LiveSearch.StartTime != DateTime.MinValue)
                {
                    var elapsed = DateTime.Now - Settings.LiveSearch.StartTime;
                    ImGui.Text($"Time: {elapsed.TotalSeconds:F0}s");
                }
                
                // Rate limit status
                if (_rateLimiter != null)
                {
                    ImGui.Spacing();
                    ImGui.Text($"Rate Limit: {_rateLimiter.GetStatus()}");
                }
                
                ImGui.Unindent();

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                // Listener status
                if (_listeners.Count > 0)
                {
                    if (ImGui.CollapsingHeader("Listeners", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        ImGui.Indent();
                        foreach (var listener in _listeners)
                        {
                            string status = "Unknown";
                            Vector4 statusColor = new Vector4(0.7f, 0.7f, 0.7f, 1.0f);
                            
                            if (listener.IsConnecting)
                            {
                                status = "🔄 Connecting";
                                statusColor = new Vector4(1.0f, 0.7f, 0.0f, 1.0f);
                            }
                            else if (listener.IsRunning)
                            {
                                status = "✅ Connected";
                                statusColor = new Vector4(0.0f, 1.0f, 0.0f, 1.0f);
                            }
                            else if (listener.IsAuthenticationError)
                            {
                                status = "🔐 Auth Error";
                                statusColor = new Vector4(1.0f, 0.0f, 0.0f, 1.0f);
                            }
                            else
                            {
                                status = "❌ Disconnected";
                                statusColor = new Vector4(1.0f, 0.0f, 0.0f, 1.0f);
                            }
                            
                            ImGui.TextColored(statusColor, $"{listener.Config.SearchId.Value}: {status}");
                        }
                        ImGui.Unindent();
                    }
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                // Recent items (simplified)
                RecentItem[] itemsArray;
                lock (_recentItemsLock)
                {
                    itemsArray = _recentItems.ToArray();
                }
                
                if (itemsArray.Length > 0)
                {
                    if (ImGui.CollapsingHeader($"Recent Items ({itemsArray.Length})", ImGuiTreeNodeFlags.DefaultOpen))
                    {
                        ImGui.Indent();
                        for (int i = 0; i < Math.Min(itemsArray.Length, 10); i++)
                        {
                            var item = itemsArray[i];
                            ImGui.BulletText($"{item.Name} - {item.Price}");
                        }
                        if (itemsArray.Length > 10)
                        {
                            ImGui.Text($"... and {itemsArray.Length - 10} more");
                        }
                        ImGui.Unindent();
                    }
                }

                ImGui.End();
            }
        }
        catch (Exception ex)
        {
            LogError($"RenderLiveSearchGui error: {ex.Message}");
        }
    }
    
    private void StartLiveSearch()
    {
        try
        {
            Settings.LiveSearch.StartTime = DateTime.Now;
            Settings.LiveSearch.TotalItemsProcessed = 0;
            Settings.LiveSearch.SuccessfulPurchases = 0;
            Settings.LiveSearch.FailedPurchases = 0;
            _liveSearchPaused = false;
            _liveSearchStarted = true;
            
            LogMessage("LiveSearch: Starting all enabled searches");
        }
        catch (Exception ex)
        {
            LogError($"LiveSearch: Error starting - {ex.Message}");
        }
    }
    
    private void StopLiveSearch()
    {
        try
        {
            _liveSearchPaused = false;
            _liveSearchStarted = false;
            ForceStopAll();
            LogMessage("LiveSearch: Stopped all listeners");
        }
        catch (Exception ex)
        {
            LogError($"LiveSearch: Error stopping - {ex.Message}");
        }
    }
}

