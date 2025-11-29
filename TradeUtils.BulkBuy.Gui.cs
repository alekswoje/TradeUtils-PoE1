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
        // Always show GUI when BulkBuy is enabled
        if (!Settings.BulkBuy.Enable.Value) return;

        try
        {
            // Set window position
            ImGui.SetNextWindowPos(Settings.BulkBuy.WindowPosition, ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new Vector2(500, 400), ImGuiCond.FirstUseEver);

            bool showGui = true; // Always show when enabled
            if (ImGui.Begin("TradeUtils - Bulk Buy", ref showGui, 
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse))
            {
                // If window was closed (showGui became false), disable BulkBuy
                if (!showGui)
                {
                    if (_bulkBuyInProgress)
                    {
                        StopBulkBuy();
                    }
                    Settings.BulkBuy.Enable.Value = false;
                    ImGui.End();
                    return;
                }
                
                // Save window position
                Settings.BulkBuy.WindowPosition = ImGui.GetWindowPos();

                // Close button in top right
                float windowWidth = ImGui.GetWindowWidth();
                float buttonWidth = 80;
                ImGui.SetCursorPosX(windowWidth - buttonWidth - ImGui.GetStyle().WindowPadding.X);
                ImGui.SetCursorPosY(ImGui.GetStyle().WindowPadding.Y);
                
                if (ImGui.Button("Close", new Vector2(buttonWidth, 0)))
                {
                    if (_bulkBuyInProgress)
                    {
                        StopBulkBuy();
                    }
                    // Closing the GUI disables the BulkBuy sub-plugin
                    Settings.BulkBuy.Enable.Value = false;
                }

                ImGui.Spacing();
                ImGui.Spacing();

                // Title and status
                ImGui.TextColored(new Vector4(0.3f, 0.7f, 1.0f, 1.0f), "=== Bulk Item Buyer ===");
                ImGui.Spacing();

                if (_bulkBuyInProgress)
                {
                    ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "RUNNING");
                    ImGui.SameLine();
                    ImGui.Text($"[{Settings.BulkBuy.CurrentItemIndex}/{Settings.BulkBuy.CurrentItemIndex + _bulkBuyQueue.Count}]");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "STOPPED");
                }

                ImGui.Separator();
                ImGui.Spacing();

                int totalGroups = Settings.BulkBuy.Groups.Count;
                int activeSearches = Settings.BulkBuy.Groups.SelectMany(g => g.Searches).Count(s => s.Enable.Value);
                
                ImGui.Text($"Groups: {totalGroups}");
                ImGui.Text($"Active Searches: {activeSearches}");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Configure groups and searches in BulkBuy Settings");
                }

                ImGui.Spacing();

                // Control buttons
                if (!_bulkBuyInProgress)
                {
                    if (ImGui.Button("Start Bulk Buy", new Vector2(150, 30)))
                    {
                        if (activeSearches > 0)
                        {
                            StartBulkBuy();
                        }
                        else
                        {
                            LogMessage("ERROR: No enabled searches found. Enable some groups and searches in settings first.");
                        }
                    }

                    ImGui.SameLine();
                    if (activeSearches > 0)
                    {
                        ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), $"Ready to buy from {activeSearches} searches");
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "No enabled searches - configure in settings");
                    }
                }
                else
                {
                    if (ImGui.Button("Stop Bulk Buy", new Vector2(150, 30)))
                    {
                        StopBulkBuy();
                    }

                    ImGui.SameLine();
                    if (_waitingForPurchaseWindow)
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.0f, 1.0f), "Waiting for purchase window...");
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "Processing items...");
                    }
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                ImGui.Text("Statistics:");
                ImGui.Indent();
                
                int inQueue = _bulkBuyQueue.Count;
                ImGui.Text($"Queue: {inQueue} items remaining");
                ImGui.Text($"Processed: {Settings.BulkBuy.TotalItemsProcessed}");
                ImGui.Text($"Successful: {Settings.BulkBuy.SuccessfulPurchases}");
                ImGui.Text($"Failed: {Settings.BulkBuy.FailedPurchases}");
                ImGui.Text($"Total Spent: {_totalSpent}");

                if (_bulkBuyInProgress)
                {
                    var elapsed = DateTime.Now - _bulkBuyStartTime;
                    ImGui.Text($"Time: {elapsed.TotalSeconds:F0}s");

                    // Simple status line
                    string status = _waitingForPurchaseWindow ? "Waiting for purchase window" : "Processing";
                    ImGui.Text($"Status: {status}");
                    ImGui.Text($"Items Remaining (approx): {inQueue}");
                }
                else
                {
                    ImGui.Text("Status: Idle");
                }

                ImGui.Unindent();
                
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                if (ImGui.CollapsingHeader("Settings", ImGuiTreeNodeFlags.DefaultOpen))
                {
                    ImGui.Indent();

                    // POESESSID (masked)
                    string session = Settings.BulkBuy.SessionId?.Value ?? string.Empty;
                    if (ImGui.InputText("POESESSID (BulkBuy)", ref session, 128, ImGuiInputTextFlags.Password))
                    {
                        if (Settings.BulkBuy.SessionId == null)
                            Settings.BulkBuy.SessionId = new TextNode(string.Empty);
                        Settings.BulkBuy.SessionId.Value = session;
                    }

                    ImGui.Spacing();

                    // Timing Preset selector
                    string[] presetNames = { "Slow", "Fast", "SuperFast" };
                    string currentPresetStr = Settings.BulkBuy.TimingPreset?.Value ?? "Fast";
                    int currentPreset = Array.IndexOf(presetNames, currentPresetStr);
                    if (currentPreset < 0) currentPreset = 1; // Default to Fast if invalid
                    
                    if (ImGui.Combo("Timing Preset##TimingPreset", ref currentPreset, presetNames, presetNames.Length))
                    {
                        Settings.BulkBuy.TimingPreset.Value = presetNames[currentPreset];
                        ApplyTimingPreset(currentPreset);
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Slow: For slow PCs/load times\nFast: Normal PC (default)\nSuperFast: Fast PC/SSD/quick load times");
                    }

                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Text("Advanced Timing (adjust if needed):");
                    ImGui.Spacing();

                    // Advanced timing controls
                    int mouseMoveDelay = Settings.BulkBuy.MouseMoveDelay?.Value ?? 50;
                    if (ImGui.SliderInt("Mouse Move Delay (ms)##MouseMove", ref mouseMoveDelay, 0, 500))
                    {
                        Settings.BulkBuy.MouseMoveDelay.Value = mouseMoveDelay;
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Delay after moving mouse before clicking");
                    }

                    int postClickDelay = Settings.BulkBuy.PostClickDelay?.Value ?? 150;
                    if (ImGui.SliderInt("Post-Click Delay (ms)##PostClick", ref postClickDelay, 50, 1000))
                    {
                        Settings.BulkBuy.PostClickDelay.Value = postClickDelay;
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Delay after clicking before checking purchase");
                    }

                    int tokenDelay = Settings.BulkBuy.HideoutTokenDelay?.Value ?? 150;
                    if (ImGui.SliderInt("Hideout Token Delay (ms)##TokenDelay", ref tokenDelay, 50, 500))
                    {
                        Settings.BulkBuy.HideoutTokenDelay.Value = tokenDelay;
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Delay after sending hideout token");
                    }

                    int retryDelay = Settings.BulkBuy.RetryDelay?.Value ?? 300;
                    if (ImGui.SliderInt("Retry Delay (ms)##RetryDelay", ref retryDelay, 100, 1000))
                    {
                        Settings.BulkBuy.RetryDelay.Value = retryDelay;
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Delay between retry attempts");
                    }

                    ImGui.Spacing();

                    // Timeout
                    int timeout = Settings.BulkBuy.TimeoutPerItem.Value;
                    if (ImGui.SliderInt("Timeout (s)##Timeout", ref timeout, 1, 10))
                    {
                        Settings.BulkBuy.TimeoutPerItem.Value = timeout;
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Max time to wait for purchase window");
                    }

                    ImGui.Spacing();

                    // Safety options
                    bool autoResume = Settings.BulkBuy.AutoResumeAfterRateLimit.Value;
                    if (ImGui.Checkbox("Auto-Resume After Rate Limit##AutoResume", ref autoResume))
                    {
                        Settings.BulkBuy.AutoResumeAfterRateLimit.Value = autoResume;
                    }

                    bool stopOnError = Settings.BulkBuy.StopOnError.Value;
                    if (ImGui.Checkbox("Stop on Error##StopOnError", ref stopOnError))
                    {
                        Settings.BulkBuy.StopOnError.Value = stopOnError;
                    }

                    bool retry = Settings.BulkBuy.RetryFailedItems.Value;
                    if (ImGui.Checkbox("Retry Failed Items##Retry", ref retry))
                    {
                        Settings.BulkBuy.RetryFailedItems.Value = retry;
                    }

                    if (Settings.BulkBuy.RetryFailedItems.Value)
                    {
                        int maxRetries = Settings.BulkBuy.MaxRetriesPerItem.Value;
                        if (ImGui.SliderInt("Max Retries##MaxRetries", ref maxRetries, 0, 5))
                        {
                            Settings.BulkBuy.MaxRetriesPerItem.Value = maxRetries;
                        }
                    }

                    ImGui.Spacing();

                    // Logging options
                    bool logToFile = Settings.BulkBuy.LogPurchasesToFile.Value;
                    if (ImGui.Checkbox("Log to File##LogToFile", ref logToFile))
                    {
                        Settings.BulkBuy.LogPurchasesToFile.Value = logToFile;
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Save purchase log to CSV file");
                    }

                    bool playSound = Settings.BulkBuy.PlaySoundOnComplete.Value;
                    if (ImGui.Checkbox("Sound on Complete##PlaySound", ref playSound))
                    {
                        Settings.BulkBuy.PlaySoundOnComplete.Value = playSound;
                    }

                    ImGui.Unindent();
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                // Current item info
                if (_currentBulkBuyItem != null)
                {
                    if (ImGui.CollapsingHeader("Current Item"))
                    {
                        ImGui.Indent();
                        ImGui.Text($"Name: {_currentBulkBuyItem.Name}");
                        ImGui.Text($"Price: {_currentBulkBuyItem.Price}");
                        ImGui.Text($"Position: ({_currentBulkBuyItem.X}, {_currentBulkBuyItem.Y})");
                        ImGui.Text($"Seller: {_currentBulkBuyItem.AccountName}");
                        ImGui.Text($"Online: {(_currentBulkBuyItem.IsOnline ? "Yes" : "No")}");
                        
                        if (_bulkBuyRetryCount > 0)
                        {
                            ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.0f, 1.0f), 
                                $"Retry: {_bulkBuyRetryCount}/{Settings.BulkBuy.MaxRetriesPerItem.Value}");
                        }
                        
                        ImGui.Unindent();
                    }
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                // Help section
                if (ImGui.CollapsingHeader("Help & Info"))
                {
                    ImGui.Indent();
                    ImGui.TextWrapped("1. Go to BulkBuy Settings to configure groups and searches");
                    ImGui.TextWrapped("2. Add groups and paste trade search URLs");
                    ImGui.TextWrapped("3. Enable groups and individual searches");
                    ImGui.TextWrapped("4. Configure your settings (delays, etc.)");
                    ImGui.TextWrapped("5. Make sure you're in your hideout");
                    ImGui.TextWrapped("6. Click 'Start Bulk Buy'");
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.0f, 1.0f), "Safety Tips:");
                    ImGui.TextWrapped("- Use delays to avoid looking like a bot");
                    ImGui.TextWrapped("- Enable randomization for more human-like behavior");
                    ImGui.TextWrapped("- Monitor the first few purchases to ensure it works");
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(0.3f, 0.7f, 1.0f, 1.0f), "Note:");
                    ImGui.TextWrapped("This feature uses GGG's official trade API.");
                    ImGui.TextWrapped("All purchases are logged for transparency.");
                    ImGui.Unindent();
                }

                // Debug info
                if (Settings.BulkBuy.DebugMode.Value)
                {
                    ImGui.Spacing();
                    ImGui.Separator();
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1.0f, 0.0f, 1.0f, 1.0f), "DEBUG INFO");
                    ImGui.Text($"Queue Count: {_bulkBuyQueue.Count}");
                    ImGui.Text($"Current Item: {(_currentBulkBuyItem != null ? _currentBulkBuyItem.Name : "None")}");
                    ImGui.Text($"In Progress: {_bulkBuyInProgress}");
                    ImGui.Text($"Waiting for Window: {_waitingForPurchaseWindow}");
                    ImGui.Text($"Retry Count: {_bulkBuyRetryCount}");
                    ImGui.Text($"Last Action: {(DateTime.Now - _lastBulkBuyActionTime).TotalSeconds:F1}s ago");
                    
                    if (_rateLimiter != null)
                    {
                        ImGui.Text($"Rate Limit: {_rateLimiter.GetStatus()}");
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
}

