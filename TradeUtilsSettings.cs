using System;
using System.Collections.Generic;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using System.Numerics;
using System.Windows.Forms;
using TradeUtils.Utility;

namespace TradeUtils;

public class TradeUtilsSettings : ISettings
{
    public TradeUtilsSettings()
    {
        LiveSearch = new LiveSearchSubSettings();
        LowerPrice = new LowerPriceSubSettings();
        BulkBuy = new BulkBuySubSettings();
    }

    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Live Search Settings")]
    public LiveSearchSubSettings LiveSearch { get; set; }
    
    [Menu("Lower Price Settings")]
    public LowerPriceSubSettings LowerPrice { get; set; }
    
    [Menu("Bulk Buy Settings")]
    public BulkBuySubSettings BulkBuy { get; set; }
}

// ==================== LIVESEARCH SUB-PLUGIN SETTINGS ====================
[Submenu(CollapsedByDefault = true)]
public class LiveSearchSubSettings
{
    public LiveSearchSubSettings()
    {
        General = new GeneralSettingsSubMenu(this);
        GroupsConfig = new GroupsRenderer(this);
    }

    // ===== SUBSECTIONS =====
    [Submenu(CollapsedByDefault = true)]
    public GeneralSettingsSubMenu General { get; set; }

    // ===== HIDDEN/INTERNAL PROPERTIES =====
    [IgnoreMenu]
    public TextNode SessionId { get; set; } = new TextNode("");

    // Secure session ID storage - not serialized to JSON
    [JsonIgnore]
    public string SecureSessionId
    {
        get => EncryptedSettings.GetSecureSessionId();
        set => EncryptedSettings.StoreSecureSessionId(value);
    }
    [Submenu(CollapsedByDefault = true)]
    public SearchSettingsSubMenu SearchSettings { get; set; } = new SearchSettingsSubMenu();

    [Submenu(CollapsedByDefault = true)]
    public AutoFeaturesSubMenu AutoFeatures { get; set; } = new AutoFeaturesSubMenu();

    [Submenu(CollapsedByDefault = true)]
    public FastModeSubMenu FastMode { get; set; } = new FastModeSubMenu();

    [Submenu(CollapsedByDefault = true)]
    public RateLimitingSubMenu RateLimiting { get; set; } = new RateLimitingSubMenu();

    // ===== INTERNAL SETTINGS =====
    [JsonIgnore]
    public Vector2 WindowPosition { get; set; } = new Vector2(10, 800);

    public List<SearchGroup> Groups { get; set; } = new List<SearchGroup>();

    [JsonIgnore]
    public GroupsRenderer GroupsConfig { get; set; }

    [JsonIgnore]
    public int RestartCooldownSeconds { get; set; } = 300;

    [Submenu(RenderMethod = nameof(Render))]
    public class GroupsRenderer
    {
        private readonly LiveSearchSubSettings _parent;
        private readonly Dictionary<string, string> _groupNameBuffers = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _searchNameBuffers = new Dictionary<string, string>();

        // Reference to the plugin instance for calling methods
        public TradeUtils PluginInstance { get; set; }

        public GroupsRenderer(LiveSearchSubSettings parent)
        {
            _parent = parent;
        }

        private static void HelpMarker(string desc)
        {
            if (!string.IsNullOrEmpty(desc))
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(?)");
                if (ImGui.BeginItemTooltip())
                {
                    ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35.0f);
                    ImGui.TextUnformatted(desc);
                    ImGui.PopTextWrapPos();
                    ImGui.EndTooltip();
                }
            }
        }

        public void Render()
        {
            ImGui.Text("Groups:");
            HelpMarker("💡 Tip: Shift+Click group or search names to quickly toggle enable/disable");
            ImGui.Separator();
            var tempGroups = new List<SearchGroup>(_parent.Groups);
            for (int i = 0; i < tempGroups.Count; i++)
            {
                var group = tempGroups[i];
                var groupIdKey = $"group{i}";
                if (!_groupNameBuffers.ContainsKey(groupIdKey))
                {
                    _groupNameBuffers[groupIdKey] = group.Name.Value;
                }
                var groupNameBuffer = _groupNameBuffers[groupIdKey];
                groupNameBuffer = group.Name.Value; // Sync buffer with current value

                bool groupEnabled = group.Enable.Value;
                bool isOpen = ImGui.CollapsingHeader($"Group##group{i}"); // Static ID for header

                // Handle shift-click on the header
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && ImGui.GetIO().KeyShift)
                {
                    group.Enable.Value = !group.Enable.Value;
                    groupEnabled = group.Enable.Value; // Update local state immediately
                }

                ImGui.SameLine();

                // Simple ON/OFF text with color
                if (groupEnabled)
                {
                    ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "[ON]"); // Green ON for enabled
                }
                else
                {
                    ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "[OFF]"); // Red OFF for disabled
                }

                ImGui.SameLine();
                ImGui.Text(group.Name.Value); // Display dynamic name

                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    ImGui.OpenPopup($"RemoveGroupContext{i}");
                }
                if (ImGui.BeginPopup($"RemoveGroupContext{i}"))
                {
                    if (ImGui.Selectable("Remove Group"))
                    {
                        tempGroups.RemoveAt(i);
                        _groupNameBuffers.Remove(groupIdKey);
                        i--;
                    }
                    ImGui.EndPopup();
                }
                if (isOpen)
                {
                    ImGui.Indent();
                    if (ImGui.InputText($"Name##group{i}", ref groupNameBuffer, 100))
                    {
                        group.Name.Value = groupNameBuffer; // Update dynamically as they type
                    }
                    var enableGroup = group.Enable.Value;
                    ImGui.Checkbox($"Enable##group{i}", ref enableGroup);
                    group.Enable.Value = enableGroup;
                    HelpMarker("Enable or disable this group; right-click header to delete group");
                    var url = group.TradeUrl.Value.Trim();
                    string urlBuffer = url;
                    if (ImGui.InputText($"Add from URL##group{i}", ref urlBuffer, 100))
                    {
                        group.TradeUrl.Value = urlBuffer;
                    }
                    HelpMarker("Enter a trade search URL to add searches");
                    if (ImGui.Button($"Add Search from URL##group{i}"))
                    {
                        if (string.IsNullOrWhiteSpace(urlBuffer))
                        {
                            ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "Error: URL cannot be empty.");
                        }
                        else
                        {
                            Uri uri;
                            try
                            {
                                uri = new Uri(urlBuffer.StartsWith("http") ? urlBuffer : $"https://www.pathofexile.com/trade/search/Standard/{urlBuffer}/live");
                            }
                            catch (UriFormatException)
                            {
                                ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "Error: Invalid URL format.");
                                return;
                            }
                            var segments = uri.AbsolutePath.TrimStart('/').Split('/');
                            if (segments.Length >= 4 && segments[0] == "trade" && segments[1] == "search" && (segments.Length == 4 || segments[4] == "live"))
                            {
                                var league = Uri.UnescapeDataString(segments[2]);
                                var searchId = segments[3];
                                group.Searches.Add(new LiveSearchInstanceSettings
                                {
                                    League = new TextNode(league),
                                    SearchId = new TextNode(searchId),
                                    Name = new TextNode($"Search {group.Searches.Count + 1}"),
                                    Enable = new ToggleNode(false)
                                });
                                ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), $"Added search: {searchId} in {league}");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "Error: URL must match trade search format.");
                            }
                            group.TradeUrl.Value = "";
                        }
                    }
                    var tempSearches = new List<LiveSearchInstanceSettings>(group.Searches);
                    for (int j = 0; j < tempSearches.Count; j++)
                    {
                        var search = tempSearches[j];
                        var searchIdKey = $"search{i}{j}";
                        if (!_searchNameBuffers.ContainsKey(searchIdKey))
                        {
                            _searchNameBuffers[searchIdKey] = search.Name.Value;
                        }
                        var searchNameBuffer = _searchNameBuffers[searchIdKey];
                        searchNameBuffer = search.Name.Value; // Sync buffer with current value

                        bool searchEnabled = search.Enable.Value;
                        bool sOpen = ImGui.CollapsingHeader($"Search##search{i}{j}"); // Static ID for header

                        // Handle shift-click on the header
                        if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && ImGui.GetIO().KeyShift)
                        {
                            search.Enable.Value = !search.Enable.Value;
                            searchEnabled = search.Enable.Value; // Update local state immediately
                        }

                        ImGui.SameLine();

                        // Simple ON/OFF text with color
                        if (searchEnabled)
                        {
                            ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "[ON]"); // Green ON for enabled
                        }
                        else
                        {
                            ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "[OFF]"); // Red OFF for disabled
                        }

                        ImGui.SameLine();
                        ImGui.Text(search.Name.Value); // Display dynamic name

                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        {
                            tempSearches.RemoveAt(j);
                            _searchNameBuffers.Remove(searchIdKey);
                            j--;
                        }
                        if (sOpen)
                        {
                            ImGui.Indent();
                            var senable = search.Enable.Value;
                            ImGui.Checkbox($"Enable##search{i}{j}", ref senable);
                            search.Enable.Value = senable;
                            HelpMarker("Enable or disable this search; right-click header to delete search");
                            if (ImGui.InputText($"Name##search{i}{j}", ref searchNameBuffer, 100))
                            {
                                search.Name.Value = searchNameBuffer; // Update dynamically as they type
                            }
                            var league = search.League.Value;
                            ImGui.InputText($"League##search{i}{j}", ref league, 100);
                            search.League.Value = league;
                            HelpMarker("League for this search");
                            var searchId = search.SearchId.Value;
                            ImGui.InputText($"Search ID##search{i}{j}", ref searchId, 100);
                            search.SearchId.Value = searchId;
                            HelpMarker("Unique ID for the trade search");

                            // Add "Open in Browser" button for individual search
                            if (!string.IsNullOrWhiteSpace(searchId))
                            {
                                ImGui.SameLine();
                                if (ImGui.Button($"🌐##search{i}{j}"))
                                {
                                    // Open this specific search in browser
                                    var searchLeague = search.League.Value;
                                    if (string.IsNullOrWhiteSpace(searchLeague))
                                    {
                                        searchLeague = "Standard";
                                    }
                                    string searchUrl = $"https://www.pathofexile.com/trade/search/{Uri.EscapeDataString(searchLeague)}/{searchId}";
                                    System.Diagnostics.Process.Start("cmd", $"/c start {searchUrl}");
                                }
                                if (ImGui.IsItemHovered())
                                {
                                    ImGui.SetTooltip($"Open {search.Name.Value} in browser");
                                }
                            }
                            else
                            {
                                ImGui.SameLine();
                                ImGui.TextDisabled("🌐");
                                if (ImGui.IsItemHovered())
                                {
                                    ImGui.SetTooltip("Enter a Search ID to enable browser opening");
                                }
                            }

                            ImGui.Unindent();
                        }
                    }
                    group.Searches = tempSearches;
                    ImGui.Unindent();
                }
                ImGui.Separator();
            }
            _parent.Groups = tempGroups;
            if (ImGui.Button("Add Group"))
            {
                _parent.Groups.Add(new SearchGroup
                {
                    Name = new TextNode($"Group {_parent.Groups.Count + 1}"),
                    Enable = new ToggleNode(false),
                    Searches = new List<LiveSearchInstanceSettings>(),
                    TradeUrl = new TextNode("")
                });
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Add "Open All Enabled Searches" button
            if (ImGui.Button("🌐 Open All Enabled Searches in Browser"))
            {
                if (PluginInstance != null)
                {
                    PluginInstance.OpenAllEnabledSearchesInBrowser();
                }
                else
                {
                    // Fallback: manually open searches
                    var enabledSearches = new List<(string searchId, string league, string name)>();

                    foreach (var group in _parent.Groups)
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

                    if (enabledSearches.Count > 0)
                    {
                        foreach (var (searchId, searchLeague, name) in enabledSearches)
                        {
                            var finalLeague = string.IsNullOrWhiteSpace(searchLeague) ? "Standard" : searchLeague;
                            string searchUrl = $"https://www.pathofexile.com/trade/search/{Uri.EscapeDataString(finalLeague)}/{searchId}";
                            System.Diagnostics.Process.Start("cmd", $"/c start {searchUrl}");

                            // Add configurable delay between opening tabs
                            int delayMs = _parent.SearchSettings.BrowserTabDelay.Value * 1000; // Convert seconds to milliseconds
                            System.Threading.Thread.Sleep(delayMs);
                        }
                    }
                }
            }
            HelpMarker("Opens all enabled searches in your default browser as separate tabs with configurable delay");
        }
    }

}

public class SearchGroup
{
    public TextNode Name { get; set; } = new TextNode("New Group");
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    public List<LiveSearchInstanceSettings> Searches { get; set; } = new List<LiveSearchInstanceSettings>();
    public TextNode TradeUrl { get; set; } = new TextNode("");
}

public class LiveSearchInstanceSettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    public TextNode Name { get; set; } = new TextNode("New Search");
    public TextNode League { get; set; } = new TextNode("Standard");
    public TextNode SearchId { get; set; } = new TextNode("");
}

// ==================== LIVESEARCH SUBSECTIONS ====================
[Submenu(CollapsedByDefault = false)]
public class GeneralSettingsSubMenu
{
    private readonly LiveSearchSubSettings _parent;

    public GeneralSettingsSubMenu(LiveSearchSubSettings parent)
    {
        _parent = parent;
        SessionIdConfig = new SessionIdRenderer(parent);
    }

    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    public ToggleNode DebugMode { get; set; } = new ToggleNode(false);

    public ToggleNode ShowGui { get; set; } = new ToggleNode(true);

    public ToggleNode PlaySound { get; set; } = new ToggleNode(true);

    public HotkeyNode TravelHotkey { get; set; } = new HotkeyNode(Keys.None);

    public HotkeyNode StopAllHotkey { get; set; } = new HotkeyNode(Keys.None);

    public SessionIdRenderer SessionIdConfig { get; set; }

    [Submenu(RenderMethod = nameof(Render))]
    public class SessionIdRenderer
    {
        private readonly LiveSearchSubSettings _parent;
        private string _sessionIdBuffer = "";
        private bool _sessionIdUpdated = false;

        public SessionIdRenderer(LiveSearchSubSettings parent)
        {
            _parent = parent;
        }

        public void Render()
        {
            // Initialize buffer from stored value if empty
            if (string.IsNullOrEmpty(_sessionIdBuffer))
            {
                // Try secure storage first, then fallback to regular TextNode
                _sessionIdBuffer = _parent.SecureSessionId ?? "";
                if (string.IsNullOrEmpty(_sessionIdBuffer))
                {
                    _sessionIdBuffer = _parent.SessionId.Value ?? "";
                    // If found in regular storage, migrate to secure
                    if (!string.IsNullOrEmpty(_sessionIdBuffer))
                    {
                        _parent.SecureSessionId = _sessionIdBuffer;
                    }
                }
            }

            ImGui.Text("Session ID:");
            ImGui.SameLine();
            if (ImGui.InputText("##SessionId", ref _sessionIdBuffer, 100, ImGuiInputTextFlags.Password))
            {
                if (!_sessionIdUpdated)
                {
                    _parent.SessionId.Value = _sessionIdBuffer;
                    _parent.SecureSessionId = _sessionIdBuffer;
                    _sessionIdUpdated = true;
                }
            }

            if (!ImGui.IsItemActive())
            {
                _sessionIdUpdated = false;
            }
        }
    }
}

[Submenu(CollapsedByDefault = false)]
public class SearchSettingsSubMenu
{
    [Menu("Search Queue Delay (ms)", "Delay between starting live searches (250-10000ms)")]
    public RangeNode<int> SearchQueueDelay { get; set; } = new RangeNode<int>(1000, 250, 10000);

    [Menu("Max Recent Items", "Maximum number of recent items to keep in the list")]
    public RangeNode<int> MaxRecentItems { get; set; } = new RangeNode<int>(5, 1, 20);

    [Menu("Log Search Results", "Enable logging of search results to a text file")]
    public ToggleNode LogSearchResults { get; set; } = new ToggleNode(true);

    [Menu("Browser Tab Delay", "Delay between opening browser tabs (seconds)")]
    public RangeNode<int> BrowserTabDelay { get; set; } = new RangeNode<int>(5, 1, 20);
}

[Submenu(CollapsedByDefault = false)]
public class AutoFeaturesSubMenu
{
    [Menu("Auto Teleport", "Automatically teleport to items")]
    public ToggleNode AutoTp { get; set; } = new ToggleNode(false);

    [Menu("Move Mouse to Item", "Move mouse cursor to highlighted items")]
    public ToggleNode MoveMouseToItem { get; set; } = new ToggleNode(true);

    [Menu("Auto Buy", "Automatically Ctrl+Left Click after moving mouse to item")]
    public ToggleNode AutoBuy { get; set; } = new ToggleNode(false);

    [Menu("Auto Stash", "Automatically stash items when inventory is full")]
    public ToggleNode AutoStash { get; set; } = new ToggleNode(false);
}

[Submenu(CollapsedByDefault = false)]
public class FastModeSubMenu
{
    [Menu("Fast Mode", "Bypass window checks and directly click after teleport")]
    public ToggleNode FastMode { get; set; } = new ToggleNode(false);

    [Menu("Fast Mode Click Delay (ms)", "Delay between clicks. Keep low for speed; raise if your client stutters.")]
    public RangeNode<int> FastModeClickDelayMs { get; set; } = new RangeNode<int>(100, 10, 1000);

    [Menu("Fast Mode Click Duration (s)", "How long to keep clicking after teleport. In seconds, because stash data can take between ~0.1s and 2–3s to fully load in hideouts.")]
    public RangeNode<float> FastModeClickDurationSec { get; set; } = new RangeNode<float>(2.5f, 0.1f, 5.0f);
}

[Submenu(CollapsedByDefault = false)]
public class RateLimitingSubMenu
{
    [Menu("Rate Limit Safety Threshold (%)", "Block requests when remaining quota drops below this percentage")]
    public RangeNode<int> RateLimitSafetyThreshold { get; set; } = new RangeNode<int>(10, 5, 25);

    [Menu("Burst Protection", "Enable protection against processing too many items at once")]
    public ToggleNode BurstProtection { get; set; } = new ToggleNode(true);

    [Menu("Max Items Per Second", "Maximum number of items to process per second")]
    public RangeNode<int> MaxItemsPerSecond { get; set; } = new RangeNode<int>(3, 1, 10);

    [Menu("Burst Queue Size", "Maximum number of items to queue for processing")]
    public RangeNode<int> BurstQueueSize { get; set; } = new RangeNode<int>(20, 0, 100);
}

// ==================== LOWERPRICE SUB-PLUGIN SETTINGS ====================
[Submenu(CollapsedByDefault = true)]
public class LowerPriceSubSettings
{
    [Menu("Enable LowerPrice", "Enable or disable the LowerPrice sub-plugin")]
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    // Grouped submenus
    [Submenu(CollapsedByDefault = true)] public LpActionTimingSubMenu ActionTiming { get; set; }
    [Submenu(CollapsedByDefault = true)] public LpCurrencySelectionSubMenu CurrencySelection { get; set; }
    [Submenu(CollapsedByDefault = true)] public LpPricingStrategySubMenu PricingStrategy { get; set; }
    [Submenu(CollapsedByDefault = true)] public LpOverridesSubMenu Overrides { get; set; }
    [Submenu(CollapsedByDefault = true)] public LpSpecialActionsSubMenu SpecialActions { get; set; }
    [Submenu(CollapsedByDefault = true)] public LpTimerNotificationsSubMenu TimerNotifications { get; set; }
    [Submenu(CollapsedByDefault = true)] public LpValueDisplaySubMenu ValueDisplay { get; set; }
    [Submenu(CollapsedByDefault = true)] public LpCurrencyRatesSubMenu CurrencyRates { get; set; }
    [Submenu(CollapsedByDefault = true)] public LpUiDebugSubMenu UiAndDebug { get; set; }

    public LowerPriceSubSettings()
    {
        ActionTiming = new LpActionTimingSubMenu(this);
        CurrencySelection = new LpCurrencySelectionSubMenu(this);
        PricingStrategy = new LpPricingStrategySubMenu(this);
        Overrides = new LpOverridesSubMenu(this);
        SpecialActions = new LpSpecialActionsSubMenu(this);
        TimerNotifications = new LpTimerNotificationsSubMenu(this);
        ValueDisplay = new LpValueDisplaySubMenu(this);
        CurrencyRates = new LpCurrencyRatesSubMenu(this);
        UiAndDebug = new LpUiDebugSubMenu(this);
    }

    [Menu("Action Delay (ms)", "Delay between actions to simulate human behavior")]
    [IgnoreMenu]
    public RangeNode<int> ActionDelay { get; set; } = new RangeNode<int>(75, 50, 1000);

    [Menu("Random Delay (ms)", "Random delay added to action delay (0-100ms)")]
    [IgnoreMenu]
    public RangeNode<int> RandomDelay { get; set; } = new RangeNode<int>(25, 0, 100);

    // ===== CURRENCY SELECTION =====
    [Menu("Reprice Chaos Orb", "Enable repricing for Chaos Orbs")]
    [IgnoreMenu]
    public ToggleNode RepriceChaos { get; set; } = new ToggleNode(true);

    [Menu("Reprice Divine Orb", "Enable repricing for Divine Orbs")]
    [IgnoreMenu]
    public ToggleNode RepriceDivine { get; set; } = new ToggleNode(true);

    [Menu("Reprice Exalted Orb", "Enable repricing for Exalted Orbs")]
    [IgnoreMenu]
    public ToggleNode RepriceExalted { get; set; } = new ToggleNode(true);

    [Menu("Reprice Annul Orb", "Enable repricing for Annul Orbs")]
    [IgnoreMenu]
    public ToggleNode RepriceAnnul { get; set; } = new ToggleNode(true);

    // ===== PRICING STRATEGY =====
    [Menu("Use Flat Reduction", "Use flat number reduction instead of percentage")]
    [IgnoreMenu]
    public ToggleNode UseFlatReduction { get; set; } = new ToggleNode(false);

    [Menu("Price Ratio", "Multiplier for item prices (0.0–1.0)")]
    [IgnoreMenu]
    public RangeNode<float> PriceRatio { get; set; } = new RangeNode<float>(0.9f, 0.0f, 1.0f);

    [Menu("Flat Reduction Amount", "Amount to subtract from item prices")]
    [IgnoreMenu]
    public RangeNode<int> FlatReductionAmount { get; set; } = new RangeNode<int>(1, 1, 100);

    // Currency-specific overrides
    [Menu("Divine Override", "Force flat reduction for Divine Orbs (overrides global setting)")]
    [IgnoreMenu]
    public ToggleNode DivineUseFlat { get; set; } = new ToggleNode(true);

    [Menu("Chaos Override", "Force flat reduction for Chaos Orbs (overrides global setting)")]
    [IgnoreMenu]
    public ToggleNode ChaosUseRatio { get; set; } = new ToggleNode(true);

    [Menu("Exalted Override", "Force flat reduction for Exalted Orbs (overrides global setting)")]
    [IgnoreMenu]
    public ToggleNode ExaltedUseRatio { get; set; } = new ToggleNode(true);

    [Menu("Annul Override", "Force flat reduction for Annul Orbs (overrides global setting)")]
    [IgnoreMenu]
    public ToggleNode AnnulUseFlat { get; set; } = new ToggleNode(true);

    // ===== SPECIAL ACTIONS =====
    [Menu("Pickup Items at 1 Currency", "Control-left-click items priced at 1 instead of repricing")]
    [IgnoreMenu]
    public ToggleNode PickupItemsAtOne { get; set; } = new ToggleNode(false);

    [Menu("Reprice Hotkey", "Hotkey to trigger repricing manually")]
    [IgnoreMenu]
    public HotkeyNode ManualRepriceHotkey { get; set; } = new HotkeyNode(Keys.None);

    // ===== TIMER & NOTIFICATIONS =====
    [Menu("Enable Timer", "Enable timer functionality")]
    [IgnoreMenu]
    public ToggleNode EnableTimer { get; set; } = new ToggleNode(false);

    [Menu("Timer Duration (minutes)", "How long to wait before playing sound notification")]
    [IgnoreMenu]
    public RangeNode<int> TimerDurationMinutes { get; set; } = new RangeNode<int>(60, 1, 300);

    [Menu("Show Timer Countdown", "Display countdown timer on screen")]
    [IgnoreMenu]
    public ToggleNode ShowTimerCountdown { get; set; } = new ToggleNode(true);

    [Menu("Enable Sound Notification", "Play sound when timer expires")]
    [IgnoreMenu]
    public ToggleNode EnableSoundNotification { get; set; } = new ToggleNode(true);

    // ===== VALUE DISPLAY =====
    [Menu("Show Value Display", "Display total value of items in merchant panel")]
    [IgnoreMenu]
    public ToggleNode ShowValueDisplay { get; set; } = new ToggleNode(true);

    [Menu("Value Display Position X", "X position of value display")]
    [IgnoreMenu]
    public RangeNode<int> ValueDisplayX { get; set; } = new RangeNode<int>(10, 0, 2000);

    [Menu("Value Display Position Y", "Y position of value display")]
    [IgnoreMenu]
    public RangeNode<int> ValueDisplayY { get; set; } = new RangeNode<int>(100, 0, 2000);

    [Menu("Auto-Update Currency Rates", "Automatically fetch currency rates from poe.ninja")]
    [IgnoreMenu]
    public ToggleNode AutoUpdateRates { get; set; } = new ToggleNode(true);

    [Menu("Currency Update Interval (minutes)", "How often to update currency rates")]
    [IgnoreMenu]
    public RangeNode<int> CurrencyUpdateInterval { get; set; } = new RangeNode<int>(30, 5, 120);
    
    [Menu("Debug Mode", "Enable detailed logging for debugging")]
    [IgnoreMenu]
    public ToggleNode DebugMode { get; set; } = new ToggleNode(false);
    
    [Menu("Show GUI", "Display the graphical user interface")]
    [IgnoreMenu]
    public ToggleNode ShowGui { get; set; } = new ToggleNode(true);
}

// ===== LOWER PRICE SUBMENU CLASSES =====
public class LpActionTimingSubMenu
{
    private readonly LowerPriceSubSettings _p;
    public LpActionTimingSubMenu(LowerPriceSubSettings p) { _p = p; }
    [Menu("Action Delay (ms)")] public RangeNode<int> ActionDelay => _p.ActionDelay;
    [Menu("Random Delay (ms)")] public RangeNode<int> RandomDelay => _p.RandomDelay;
}

public class LpCurrencySelectionSubMenu
{
    private readonly LowerPriceSubSettings _p;
    public LpCurrencySelectionSubMenu(LowerPriceSubSettings p) { _p = p; }
    [Menu("Reprice Chaos Orb")] public ToggleNode RepriceChaos => _p.RepriceChaos;
    [Menu("Reprice Divine Orb")] public ToggleNode RepriceDivine => _p.RepriceDivine;
    [Menu("Reprice Exalted Orb")] public ToggleNode RepriceExalted => _p.RepriceExalted;
    [Menu("Reprice Annul Orb")] public ToggleNode RepriceAnnul => _p.RepriceAnnul;
}

public class LpPricingStrategySubMenu
{
    private readonly LowerPriceSubSettings _p;
    public LpPricingStrategySubMenu(LowerPriceSubSettings p) { _p = p; }
    [Menu("Use Flat Reduction")] public ToggleNode UseFlatReduction => _p.UseFlatReduction;
    [Menu("Price Ratio")] public RangeNode<float> PriceRatio => _p.PriceRatio;
    [Menu("Flat Reduction Amount")] public RangeNode<int> FlatReductionAmount => _p.FlatReductionAmount;
}

public class LpOverridesSubMenu
{
    private readonly LowerPriceSubSettings _p;
    public LpOverridesSubMenu(LowerPriceSubSettings p) { _p = p; }
    [Menu("Divine Override")] public ToggleNode DivineUseFlat => _p.DivineUseFlat;
    [Menu("Chaos Override")] public ToggleNode ChaosUseRatio => _p.ChaosUseRatio;
    [Menu("Exalted Override")] public ToggleNode ExaltedUseRatio => _p.ExaltedUseRatio;
    [Menu("Annul Override")] public ToggleNode AnnulUseFlat => _p.AnnulUseFlat;
}

public class LpSpecialActionsSubMenu
{
    private readonly LowerPriceSubSettings _p;
    public LpSpecialActionsSubMenu(LowerPriceSubSettings p) { _p = p; }
    [Menu("Pickup Items at 1 Currency")] public ToggleNode PickupItemsAtOne => _p.PickupItemsAtOne;
    [Menu("Reprice Hotkey")] public HotkeyNode ManualRepriceHotkey => _p.ManualRepriceHotkey;
}

public class LpTimerNotificationsSubMenu
{
    private readonly LowerPriceSubSettings _p;
    public LpTimerNotificationsSubMenu(LowerPriceSubSettings p) { _p = p; }
    [Menu("Enable Timer")] public ToggleNode EnableTimer => _p.EnableTimer;
    [Menu("Timer Duration (minutes)")] public RangeNode<int> TimerDurationMinutes => _p.TimerDurationMinutes;
    [Menu("Show Timer Countdown")] public ToggleNode ShowTimerCountdown => _p.ShowTimerCountdown;
    [Menu("Enable Sound Notification")] public ToggleNode EnableSoundNotification => _p.EnableSoundNotification;
}

public class LpValueDisplaySubMenu
{
    private readonly LowerPriceSubSettings _p;
    public LpValueDisplaySubMenu(LowerPriceSubSettings p) { _p = p; }
    [Menu("Show Value Display")] public ToggleNode ShowValueDisplay => _p.ShowValueDisplay;
    [Menu("Value Display Position X")] public RangeNode<int> ValueDisplayX => _p.ValueDisplayX;
    [Menu("Value Display Position Y")] public RangeNode<int> ValueDisplayY => _p.ValueDisplayY;
}

public class LpCurrencyRatesSubMenu
{
    private readonly LowerPriceSubSettings _p;
    public LpCurrencyRatesSubMenu(LowerPriceSubSettings p) { _p = p; }
    [Menu("Auto-Update Currency Rates")] public ToggleNode AutoUpdateRates => _p.AutoUpdateRates;
    [Menu("Currency Update Interval (minutes)")] public RangeNode<int> CurrencyUpdateInterval => _p.CurrencyUpdateInterval;
}

public class LpUiDebugSubMenu
{
    private readonly LowerPriceSubSettings _p;
    public LpUiDebugSubMenu(LowerPriceSubSettings p) { _p = p; }
    [Menu("Debug Mode")] public ToggleNode DebugMode => _p.DebugMode;
    [Menu("Show GUI")] public ToggleNode ShowGui => _p.ShowGui;
}

// ==================== BULKBUY SUB-PLUGIN SETTINGS ====================
[Submenu(CollapsedByDefault = true)]
public class BulkBuySubSettings
{
    public BulkBuySubSettings()
    {
        GroupsConfig = new BulkBuyGroupsRenderer(this);
        General = new BbGeneralSubMenu(this);
        PurchaseLimits = new BbPurchaseLimitsSubMenu(this);
        TimingDelays = new BbTimingDelaysSubMenu(this);
        Safety = new BbSafetySubMenu(this);
        Logging = new BbLoggingSubMenu(this);
    }

    // ===== MAIN SETTINGS =====
    [Menu("Enable BulkBuy", "Enable or disable the BulkBuy sub-plugin")]
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("Debug Mode", "Enable detailed logging for debugging")]
    [IgnoreMenu]
    public ToggleNode DebugMode { get; set; } = new ToggleNode(false);

    [Menu("Show GUI", "Display the graphical user interface")]
    [IgnoreMenu]
    public ToggleNode ShowGui { get; set; } = new ToggleNode(true);

    [Menu("Stop All Hotkey", "Key to emergency stop all bulk purchases")]
    [IgnoreMenu]
    public HotkeyNode StopAllHotkey { get; set; } = new HotkeyNode(Keys.None);

    // ===== SEARCH CONFIGURATION =====
    [Menu("Max Items to Buy", "Maximum number of items to purchase (0 = unlimited)")]
    [IgnoreMenu]
    public RangeNode<int> MaxItemsToBuy { get; set; } = new RangeNode<int>(10, 0, 100);

    [Menu("Max Total Spend", "Maximum total currency to spend (0 = unlimited)")]
    [IgnoreMenu]
    public RangeNode<int> MaxTotalSpend { get; set; } = new RangeNode<int>(0, 0, 10000);

    // ===== TIMING & DELAYS =====
    [Menu("Delay Between Purchases (ms)", "Delay between each purchase attempt (human-like behavior)")]
    [IgnoreMenu]
    public RangeNode<int> DelayBetweenPurchases { get; set; } = new RangeNode<int>(3000, 1000, 10000);

    [Menu("Randomize Delays", "Add random variance to delays (more human-like)")]
    [IgnoreMenu]
    public ToggleNode RandomizeDelays { get; set; } = new ToggleNode(true);

    [Menu("Random Delay Variance (ms)", "Maximum random variance to add to delays")]
    [IgnoreMenu]
    public RangeNode<int> RandomDelayVariance { get; set; } = new RangeNode<int>(1000, 0, 3000);

    [Menu("Timeout Per Item (seconds)", "Max time to wait for purchase window before moving to next item")]
    [IgnoreMenu]
    public RangeNode<int> TimeoutPerItem { get; set; } = new RangeNode<int>(15, 5, 60);

    // ===== SAFETY & LIMITS =====
    [Menu("Auto-Resume After Rate Limit", "Automatically resume after rate limit cooldown")]
    [IgnoreMenu]
    public ToggleNode AutoResumeAfterRateLimit { get; set; } = new ToggleNode(true);

    [Menu("Stop on Error", "Stop bulk buying if an error occurs")]
    [IgnoreMenu]
    public ToggleNode StopOnError { get; set; } = new ToggleNode(false);

    [Menu("Retry Failed Items", "Retry items that fail to purchase")]
    [IgnoreMenu]
    public ToggleNode RetryFailedItems { get; set; } = new ToggleNode(true);

    [Menu("Max Retries Per Item", "Maximum retry attempts per failed item")]
    [IgnoreMenu]
    public RangeNode<int> MaxRetriesPerItem { get; set; } = new RangeNode<int>(2, 0, 5);

    // ===== LOGGING =====
    [Menu("Log Purchases to File", "Save purchase log to CSV file")]
    [IgnoreMenu]
    public ToggleNode LogPurchasesToFile { get; set; } = new ToggleNode(true);

    [Menu("Play Sound on Complete", "Play notification sound when bulk buy completes")]
    [IgnoreMenu]
    public ToggleNode PlaySoundOnComplete { get; set; } = new ToggleNode(true);

    [Menu("Show Notifications", "Show toast notifications for important events")]
    [IgnoreMenu]
    public ToggleNode ShowNotifications { get; set; } = new ToggleNode(true);

    // ===== GROUPS SYSTEM (like LiveSearch) =====
    public List<BulkBuyGroup> Groups { get; set; } = new List<BulkBuyGroup>();

    [JsonIgnore]
    public BulkBuyGroupsRenderer GroupsConfig { get; set; }

    [JsonIgnore]
    public Vector2 WindowPosition { get; set; } = new Vector2(10, 400);

    // ===== INTERNAL STATE =====
    [JsonIgnore]
    public bool IsRunning { get; set; } = false;

    [JsonIgnore]
    public int CurrentItemIndex { get; set; } = 0;

    [JsonIgnore]
    public int TotalItemsProcessed { get; set; } = 0;

    [JsonIgnore]
    public int SuccessfulPurchases { get; set; } = 0;

    [JsonIgnore]
    public int FailedPurchases { get; set; } = 0;

    // Grouped submenu properties for BulkBuy
    [Submenu(CollapsedByDefault = true)] public BbGeneralSubMenu General { get; set; }
    [Submenu(CollapsedByDefault = true)] public BbPurchaseLimitsSubMenu PurchaseLimits { get; set; }
    [Submenu(CollapsedByDefault = true)] public BbTimingDelaysSubMenu TimingDelays { get; set; }
    [Submenu(CollapsedByDefault = true)] public BbSafetySubMenu Safety { get; set; }
    [Submenu(CollapsedByDefault = true)] public BbLoggingSubMenu Logging { get; set; }
}

// ===== BULK BUY SUBMENU CLASSES =====
public class BbGeneralSubMenu
{
    private readonly BulkBuySubSettings _p;
    public BbGeneralSubMenu(BulkBuySubSettings p) { _p = p; }
    [Menu("Debug Mode")] public ToggleNode DebugMode => _p.DebugMode;
    [Menu("Show GUI")] public ToggleNode ShowGui => _p.ShowGui;
    [Menu("Stop All Hotkey")] public HotkeyNode StopAllHotkey => _p.StopAllHotkey;
}

public class BbPurchaseLimitsSubMenu
{
    private readonly BulkBuySubSettings _p;
    public BbPurchaseLimitsSubMenu(BulkBuySubSettings p) { _p = p; }
    [Menu("Max Items to Buy")] public RangeNode<int> MaxItemsToBuy => _p.MaxItemsToBuy;
    [Menu("Max Total Spend")] public RangeNode<int> MaxTotalSpend => _p.MaxTotalSpend;
}

public class BbTimingDelaysSubMenu
{
    private readonly BulkBuySubSettings _p;
    public BbTimingDelaysSubMenu(BulkBuySubSettings p) { _p = p; }
    [Menu("Delay Between Purchases (ms)")] public RangeNode<int> DelayBetweenPurchases => _p.DelayBetweenPurchases;
    [Menu("Randomize Delays")] public ToggleNode RandomizeDelays => _p.RandomizeDelays;
    [Menu("Random Delay Variance (ms)")] public RangeNode<int> RandomDelayVariance => _p.RandomDelayVariance;
    [Menu("Timeout Per Item (seconds)")] public RangeNode<int> TimeoutPerItem => _p.TimeoutPerItem;
}

public class BbSafetySubMenu
{
    private readonly BulkBuySubSettings _p;
    public BbSafetySubMenu(BulkBuySubSettings p) { _p = p; }
    [Menu("Auto-Resume After Rate Limit")] public ToggleNode AutoResumeAfterRateLimit => _p.AutoResumeAfterRateLimit;
    [Menu("Stop on Error")] public ToggleNode StopOnError => _p.StopOnError;
    [Menu("Retry Failed Items")] public ToggleNode RetryFailedItems => _p.RetryFailedItems;
    [Menu("Max Retries Per Item")] public RangeNode<int> MaxRetriesPerItem => _p.MaxRetriesPerItem;
}

public class BbLoggingSubMenu
{
    private readonly BulkBuySubSettings _p;
    public BbLoggingSubMenu(BulkBuySubSettings p) { _p = p; }
    [Menu("Log Purchases to File")] public ToggleNode LogPurchasesToFile => _p.LogPurchasesToFile;
    [Menu("Play Sound on Complete")] public ToggleNode PlaySoundOnComplete => _p.PlaySoundOnComplete;
    [Menu("Show Notifications")] public ToggleNode ShowNotifications => _p.ShowNotifications;
}

// ==================== BULKBUY GROUP & SEARCH CLASSES ====================
public class BulkBuyGroup
{
    public BulkBuyGroup()
    {
        Name = new TextNode("New Group");
        Enable = new ToggleNode(false);
        Searches = new List<BulkBuySearch>();
        TradeUrl = new TextNode("");
    }

    [Menu("Group Name")]
    public TextNode Name { get; set; }

    [Menu("Enable Group")]
    public ToggleNode Enable { get; set; }

    [Menu("Trade URL")]
    public TextNode TradeUrl { get; set; }

    public List<BulkBuySearch> Searches { get; set; }
}

public class BulkBuySearch
{
    public BulkBuySearch()
    {
        Name = new TextNode("New Search");
        Enable = new ToggleNode(false);
        League = new TextNode("");
        SearchId = new TextNode("");
        MaxItems = new RangeNode<int>(10, 1, 100);
    }

    [Menu("Search Name")]
    public TextNode Name { get; set; }

    [Menu("Enable Search")]
    public ToggleNode Enable { get; set; }

    [Menu("League")]
    public TextNode League { get; set; }

    [Menu("Search ID")]
    public TextNode SearchId { get; set; }

    [Menu("Max Items")]
    public RangeNode<int> MaxItems { get; set; }
}

// ==================== BULKBUY GROUPS RENDERER ====================
[Submenu(RenderMethod = nameof(Render))]
public class BulkBuyGroupsRenderer
{
    private readonly BulkBuySubSettings _parent;
    private readonly Dictionary<string, string> _groupNameBuffers = new Dictionary<string, string>();
    private readonly Dictionary<string, string> _searchNameBuffers = new Dictionary<string, string>();

    // Reference to the plugin instance for calling methods
    public TradeUtils PluginInstance { get; set; }

    public BulkBuyGroupsRenderer(BulkBuySubSettings parent)
    {
        _parent = parent;
    }

    private static void HelpMarker(string desc)
    {
        if (!string.IsNullOrEmpty(desc))
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.BeginItemTooltip())
            {
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35.0f);
                ImGui.TextUnformatted(desc);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }
    }

    public void Render()
    {
        ImGui.Text("Groups:");
        HelpMarker("💡 Tip: Shift+Click group or search names to quickly toggle enable/disable");
        ImGui.Separator();
        var tempGroups = new List<BulkBuyGroup>(_parent.Groups);
        for (int i = 0; i < tempGroups.Count; i++)
        {
            var group = tempGroups[i];
            var groupIdKey = $"group{i}";
            if (!_groupNameBuffers.ContainsKey(groupIdKey))
            {
                _groupNameBuffers[groupIdKey] = group.Name.Value;
            }
            var groupNameBuffer = _groupNameBuffers[groupIdKey];
            groupNameBuffer = group.Name.Value; // Sync buffer with current value

            bool groupEnabled = group.Enable.Value;
            bool isOpen = ImGui.CollapsingHeader($"Group##bulkgroup{i}"); // Static ID for header

            // Handle shift-click on the header
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && ImGui.GetIO().KeyShift)
            {
                group.Enable.Value = !group.Enable.Value;
                groupEnabled = group.Enable.Value; // Update local state immediately
            }

            ImGui.SameLine();

            // Simple ON/OFF text with color
            if (groupEnabled)
            {
                ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "[ON]"); // Green ON for enabled
            }
            else
            {
                ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "[OFF]"); // Red OFF for disabled
            }

            ImGui.SameLine();
            ImGui.Text(group.Name.Value); // Display dynamic name

            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                ImGui.OpenPopup($"RemoveBulkGroupContext{i}");
            }
            if (ImGui.BeginPopup($"RemoveBulkGroupContext{i}"))
            {
                if (ImGui.Selectable("Remove Group"))
                {
                    tempGroups.RemoveAt(i);
                    _groupNameBuffers.Remove(groupIdKey);
                    i--;
                }
                ImGui.EndPopup();
            }
            if (isOpen)
            {
                ImGui.Indent();
                if (ImGui.InputText($"Name##bulkgroup{i}", ref groupNameBuffer, 100))
                {
                    group.Name.Value = groupNameBuffer; // Update dynamically as they type
                }
                var enableGroup = group.Enable.Value;
                ImGui.Checkbox($"Enable##bulkgroup{i}", ref enableGroup);
                group.Enable.Value = enableGroup;
                HelpMarker("Enable or disable this group; right-click header to delete group");
                var url = group.TradeUrl.Value.Trim();
                string urlBuffer = url;
                if (ImGui.InputText($"Add from URL##bulkgroup{i}", ref urlBuffer, 100))
                {
                    group.TradeUrl.Value = urlBuffer;
                }
                HelpMarker("Enter a trade search URL to add searches");
                if (ImGui.Button($"Add Search from URL##bulkgroup{i}"))
                {
                    if (string.IsNullOrWhiteSpace(urlBuffer))
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "Error: URL cannot be empty.");
                    }
                    else
                    {
                        Uri uri;
                        try
                        {
                            uri = new Uri(urlBuffer.StartsWith("http") ? urlBuffer : $"https://www.pathofexile.com/trade/search/Standard/{urlBuffer}");
                        }
                        catch (UriFormatException)
                        {
                            ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "Error: Invalid URL format.");
                            return;
                        }
                        var segments = uri.AbsolutePath.TrimStart('/').Split('/');
                        if (segments.Length >= 4 && segments[0] == "trade" && segments[1] == "search")
                        {
                            var league = Uri.UnescapeDataString(segments[2]);
                            var searchId = segments[3];
                            group.Searches.Add(new BulkBuySearch
                            {
                                League = new TextNode(league),
                                SearchId = new TextNode(searchId),
                                Name = new TextNode($"Search {group.Searches.Count + 1}"),
                                Enable = new ToggleNode(false)
                            });
                            ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), $"Added search: {searchId} in {league}");
                        }
                        else
                        {
                            ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "Error: URL must match trade search format.");
                        }
                        group.TradeUrl.Value = "";
                    }
                }
                var tempSearches = new List<BulkBuySearch>(group.Searches);
                for (int j = 0; j < tempSearches.Count; j++)
                {
                    var search = tempSearches[j];
                    var searchIdKey = $"search{i}{j}";
                    if (!_searchNameBuffers.ContainsKey(searchIdKey))
                    {
                        _searchNameBuffers[searchIdKey] = search.Name.Value;
                    }
                    var searchNameBuffer = _searchNameBuffers[searchIdKey];
                    searchNameBuffer = search.Name.Value; // Sync buffer with current value

                    bool searchEnabled = search.Enable.Value;
                    bool searchOpen = ImGui.CollapsingHeader($"Search##bulksearch{i}{j}"); // Static ID for header

                    // Handle shift-click on the header
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && ImGui.GetIO().KeyShift)
                    {
                        search.Enable.Value = !search.Enable.Value;
                        searchEnabled = search.Enable.Value; // Update local state immediately
                    }

                    ImGui.SameLine();

                    // Simple ON/OFF text with color
                    if (searchEnabled)
                    {
                        ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "[ON]"); // Green ON for enabled
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "[OFF]"); // Red OFF for disabled
                    }

                    ImGui.SameLine();
                    ImGui.Text(search.Name.Value); // Display dynamic name

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        ImGui.OpenPopup($"RemoveBulkSearchContext{i}{j}");
                    }
                    if (ImGui.BeginPopup($"RemoveBulkSearchContext{i}{j}"))
                    {
                        if (ImGui.Selectable("Remove Search"))
                        {
                            tempSearches.RemoveAt(j);
                            _searchNameBuffers.Remove(searchIdKey);
                            j--;
                        }
                        ImGui.EndPopup();
                    }
                    if (searchOpen)
                    {
                        ImGui.Indent();
                        if (ImGui.InputText($"Name##bulksearch{i}{j}", ref searchNameBuffer, 100))
                        {
                            search.Name.Value = searchNameBuffer; // Update dynamically as they type
                        }
                        var enableSearch = search.Enable.Value;
                        ImGui.Checkbox($"Enable##bulksearch{i}{j}", ref enableSearch);
                        search.Enable.Value = enableSearch;
                        HelpMarker("Enable or disable this search; right-click header to delete search");
                        ImGui.Text($"League: {search.League.Value}");
                        ImGui.Text($"Search ID: {search.SearchId.Value}");
                        var maxItems = search.MaxItems.Value;
                        if (ImGui.SliderInt($"Max Items##bulksearch{i}{j}", ref maxItems, 1, 100))
                        {
                            search.MaxItems.Value = maxItems;
                        }
                        HelpMarker("Maximum items to buy from this search");
                        ImGui.Unindent();
                    }
                }
                group.Searches = tempSearches;
                ImGui.Unindent();
            }
        }
        _parent.Groups = tempGroups;

        ImGui.Separator();
        if (ImGui.Button("Add New Group##BulkBuyAddGroup"))
        {
            _parent.Groups.Add(new BulkBuyGroup
            {
                Name = new TextNode($"Group {_parent.Groups.Count + 1}"),
                Enable = new ToggleNode(false),
                Searches = new List<BulkBuySearch>(),
                TradeUrl = new TextNode("")
            });
        }
        HelpMarker("Add a new group to organize your bulk buy searches");
    }
}
