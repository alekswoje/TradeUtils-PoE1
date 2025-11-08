using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using TradeUtils.Utility;
using ExileCore.Shared.Enums;
using Color = SharpDX.Color;
using Vector2 = System.Numerics.Vector2;

namespace TradeUtils;

public partial class TradeUtils
{
    public override void Render()
    {
        // Debug logging to track when Render is called during loading
        if (GameController.IsLoading)
        {
            LogDebug("🖼️ RENDER: Called during loading screen");
        }
        
        // CRITICAL: Respect plugin enable state for ALL rendering
        if (!Settings.Enable.Value)
        {
            // If plugin is disabled, ensure all listeners are stopped
            if (_listeners.Count > 0)
            {
                LogMessage($"🛑 PLUGIN DISABLED: Force stopping {_listeners.Count} active listeners from Render method");
                ForceStopAll();
            }
            return;
        }
        
        // Render LiveSearch GUI if enabled
        if (Settings.LiveSearch.Enable.Value)
        {
            RenderLiveSearchGui();
        }
        
        // Render LowerPrice if enabled
        if (Settings.LowerPrice.Enable.Value)
        {
            RenderLowerPrice();
        }

        // Render BulkBuy if enabled
        if (Settings.BulkBuy.Enable.Value)
        {
            RenderBulkBuy();
        }
        
        // Render Currency Exchange if enabled
        if (Settings.CurrencyExchange.Enable.Value)
        {
            RenderCurrencyExchange();
        }
    }
    
    private void RenderLiveSearchGui()
    {
        if (!Settings.LiveSearch.General.ShowGui.Value) return;

        // Set window position but allow auto-resizing
        ImGui.SetNextWindowPos(Settings.LiveSearch.WindowPosition, ImGuiCond.FirstUseEver);
        
        // Set minimum window size for better appearance
        ImGui.SetNextWindowSizeConstraints(new Vector2(200, 100), new Vector2(float.MaxValue, float.MaxValue));
        
        // Enable auto-resize: Remove NoResize flag and add AlwaysAutoResize
        ImGui.Begin("LiveSearch Results", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize);
        
        // Add padding for better text spacing
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 4));
        
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.1f, 0.1f, 0.1f, 0.8f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.9f, 0.9f, 1.0f));

        // Show current teleporting item if we're in the process of teleporting
        if (_currentTeleportingItem != null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.8f, 0.0f, 1.0f)); // Orange color
            ImGui.Text("🚀 Teleporting to:");
            ImGui.SameLine();
            ImGui.Text($"{_currentTeleportingItem.Name} - {_currentTeleportingItem.Price}");
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        // Show auto stash status if in progress
        if (_autoStashInProgress)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.0f, 1.0f, 1.0f, 1.0f)); // Cyan color
            ImGui.Text("📦 Auto Stash in Progress...");
            ImGui.SameLine();
            var elapsed = DateTime.Now - _autoStashStartTime;
            ImGui.Text($"({elapsed.Minutes:D2}:{elapsed.Seconds:D2})");
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        // Use child windows and proper spacing for better auto-sizing
        if (_listeners.Count > 0)
        {
            ImGui.Text("🔍 Search Listeners:");
            ImGui.Spacing();

            foreach (var listener in _listeners)
            {
                string status = "Unknown";
                if (listener.IsConnecting) status = "🔄 Connecting";
                else if (listener.IsRunning) status = "✅ Connected";
                else if (listener.IsAuthenticationError) status = "🔐 Authentication Error";
                else status = "❌ Disconnected";
                
                // Use red text for authentication errors
                if (listener.IsAuthenticationError)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF); // Red color (ABGR format)
                    ImGui.BulletText($"{listener.Config.SearchId.Value}: {status}");
                    ImGui.PopStyleColor();
                }
                else
                {
                    ImGui.BulletText($"{listener.Config.SearchId.Value}: {status}");
                }
                
                // Indent additional info
                double cooldownSeconds = listener.IsAuthenticationError ? 10 : LiveSearchSettings.RestartCooldownSeconds;
                if ((DateTime.Now - listener.LastErrorTime).TotalSeconds < cooldownSeconds ||
                    listener.ConnectionAttempts > 0)
                {
                    ImGui.Indent();
                    
                    if ((DateTime.Now - listener.LastErrorTime).TotalSeconds < cooldownSeconds)
                    {
                        float remainingCooldown = (float)(cooldownSeconds - (DateTime.Now - listener.LastErrorTime).TotalSeconds);
                        if (listener.IsAuthenticationError)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF); // Red color for auth errors
                            ImGui.Text($"🔐 Auth Error - Fix Session ID: {remainingCooldown:F1}s");
                            ImGui.PopStyleColor();
                        }
                        else
                        {
                            ImGui.Text($"⏱️ Error Cooldown: {remainingCooldown:F1}s");
                        }
                    }
                    
                    if (listener.ConnectionAttempts > 0)
                    {
                        ImGui.Text($"🔄 Attempts: {listener.ConnectionAttempts}");
                    }
                    
                    ImGui.Unindent();
                }
            }
            
            ImGui.Spacing();
            ImGui.Text($"📊 Status: {_listeners.Count(l => l.IsRunning)}/{_listeners.Count} active");
            
            // Show authentication error help if any listener has auth errors
            if (_listeners.Any(l => l.IsAuthenticationError))
            {
                ImGui.Spacing();
                ImGui.PushStyleColor(ImGuiCol.Text, 0xFF0000FF); // Red color
                ImGui.Text("🔐 Authentication Error Detected!");
                ImGui.PopStyleColor();
                ImGui.Text("💡 Fix: Update your POESESSID in settings");
                ImGui.Text("📋 Get it from browser cookies on pathofexile.com");
            }
        }
        else
        {
            ImGui.Text("🔍 No active searches");
        }
        
        RecentItem[] itemsArray;
        lock (_recentItemsLock)
        {
            itemsArray = _recentItems.ToArray(); // Convert to array to avoid modification during iteration
        }
        
        if (itemsArray.Length > 0)
        {
            ImGui.Separator();
            ImGui.Text("📦 Recent Items:");
            ImGui.Spacing();
            
            var itemsToRemove = new List<RecentItem>();
            
            for (int i = 0; i < itemsArray.Length; i++)
            {
                var item = itemsArray[i];
                
                // Use unique IDs for buttons
                ImGui.PushID($"item_{i}");
                
                // Start a horizontal layout
                ImGui.AlignTextToFramePadding();
                
                // TP Button (Door icon) - make it smaller and green
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.7f, 0.2f, 0.8f)); // Green
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.3f, 0.8f, 0.3f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.1f, 0.6f, 0.1f, 1.0f));
                
                if (ImGui.Button("🚪", new Vector2(30, 0)))
                {
                    LogMessage($"🚪 TP Button clicked for: {item.Name}");
                    TeleportToSpecificItem(item);
                }
                
                ImGui.PopStyleColor(3);
                
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"Teleport to {item.Name}");
                }
                
                ImGui.SameLine();
                
                // X Button (Remove) - make it smaller and red
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 0.8f)); // Red
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1.0f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.1f, 0.1f, 1.0f));
                
                if (ImGui.Button("❌", new Vector2(30, 0)))
                {
                    LogMessage($"❌ Remove Button clicked for: {item.Name}");
                    itemsToRemove.Add(item);
                }
                
                ImGui.PopStyleColor(3);
                
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"Remove {item.Name} from list");
                }
                
                ImGui.SameLine();
                
                // Item text
                ImGui.BulletText($"{item.Name} - {item.Price}");
                ImGui.SameLine();
                ImGui.TextDisabled($"({item.X}, {item.Y})");
                
                // Show token expiration status
                if (item.TokenExpiresAt != DateTime.MinValue)
                {
                    ImGui.SameLine();
                    var timeUntilExpiry = item.TokenExpiresAt - DateTime.Now;
                    if (timeUntilExpiry.TotalSeconds > 0)
                    {
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), $"[Expires: {timeUntilExpiry.Minutes:D2}:{timeUntilExpiry.Seconds:D2}]");
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), "[EXPIRED]");
                    }
                }
                
                ImGui.PopID();
            }
            
            // Remove items that were marked for removal
            foreach (var itemToRemove in itemsToRemove)
            {
                RemoveSpecificItem(itemToRemove);
            }
        }

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
        ImGui.End();

        if (Settings.LiveSearch.General.ShowGui.Value)
        {
            // REMOVED: Green status text display
            
            // Show current teleporting item during loading screens
            if (_currentTeleportingItem != null)
            {
                Graphics.DrawText($"🚀 Teleporting to: {_currentTeleportingItem.Name} - {_currentTeleportingItem.Price}", new Vector2(100, 120), Color.Orange, FontAlign.Left);
            }
            
            // Show loading screen indicator and additional info
            if (GameController.IsLoading)
            {
                Graphics.DrawText("⏳ Loading...", new Vector2(100, 140), Color.Yellow, FontAlign.Left);
                
                // Show recent items count
                int recentItemsCount;
                lock (_recentItemsLock)
                {
                    recentItemsCount = _recentItems.Count;
                }
                if (recentItemsCount > 0)
                {
                    Graphics.DrawText($"📦 {recentItemsCount} items in queue", new Vector2(100, 160), Color.LightBlue, FontAlign.Left);
                }
            }
        }
    }


    public override void DrawSettings()
    {
        if (!Settings.Enable.Value && _listeners.Count > 0)
        {
            LogMessage($"🛑 PLUGIN DISABLED: Force stopping {_listeners.Count} active listeners from DrawSettings method");
            ForceStopAll();
        }

        // Defer to attribute-driven settings UI only.
        base.DrawSettings();
    }

}
