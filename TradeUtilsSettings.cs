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
        CurrencyExchange = new CurrencyExchangeSubSettings();
    }

    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Live Search Settings")]
    public LiveSearchSubSettings LiveSearch { get; set; }
    
    [Menu("Lower Price Settings")]
    public LowerPriceSubSettings LowerPrice { get; set; }
    
    [Menu("Bulk Buy Settings")]
    public BulkBuySubSettings BulkBuy { get; set; }
    
    [Menu("Currency Exchange Settings")]
    public CurrencyExchangeSubSettings CurrencyExchange { get; set; }
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

    // ===== ENABLE TOGGLE (at root level) =====
    [Menu("Enable Live Search", "Enable or disable the Live Search sub-plugin")]
    public ToggleNode Enable { get; set; } = new ToggleNode(true);

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
    
    // ===== STATS TRACKING =====
    [JsonIgnore]
    public int TotalItemsProcessed { get; set; } = 0;
    
    [JsonIgnore]
    public int SuccessfulPurchases { get; set; } = 0;
    
    [JsonIgnore]
    public int FailedPurchases { get; set; } = 0;
    
    [JsonIgnore]
    public DateTime StartTime { get; set; } = DateTime.MinValue;

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
                            
                            var fastMode = search.FastMode.Value;
                            ImGui.Checkbox($"Fast Mode##search{i}{j}", ref fastMode);
                            search.FastMode.Value = fastMode;
                            HelpMarker("Enable fast mode for this search (rapid clicking)");

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
    public ToggleNode FastMode { get; set; } = new ToggleNode(false);
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
        TimingDelays = new BbTimingDelaysSubMenu(this);
        Safety = new BbSafetySubMenu(this);
        Logging = new BbLoggingSubMenu(this);
        
        // Initialize timing preset values (Fast preset by default)
        TimingPreset = new ListNode();
        TimingPreset.Value = "Fast"; // Default to Fast preset
    }

    // ===== MAIN SETTINGS =====
    [Menu("Enable BulkBuy", "Enable or disable the BulkBuy sub-plugin")]
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("Enable Item Verification", "Verify item name and price from clipboard before clicking (experimental, off by default)")]
    public ToggleNode EnableItemVerification { get; set; } = new ToggleNode(false);

    [Menu("Debug Mode", "Enable detailed logging for debugging")]
    [IgnoreMenu]
    public ToggleNode DebugMode { get; set; } = new ToggleNode(false);

    [IgnoreMenu]
    public TextNode SessionId { get; set; } = new TextNode("");

    [Menu("Toggle BulkBuy Hotkey", "Key to start/stop bulk buying")]
    [IgnoreMenu]
    public HotkeyNode ToggleHotkey { get; set; } = new HotkeyNode(Keys.None);

    [Menu("Stop All Hotkey", "Key to emergency stop all bulk purchases")]
    [IgnoreMenu]
    public HotkeyNode StopAllHotkey { get; set; } = new HotkeyNode(Keys.None);

    // ===== TIMING & DELAYS =====
    [Menu("Timing Preset", "Choose preset: Slow (slow PC/load times), Fast (normal), SuperFast (fast PC/load times)")]
    [IgnoreMenu]
    public ListNode TimingPreset { get; set; } = new ListNode(); // Default to "Fast" (index 1)

    [Menu("Timeout Per Item (seconds)", "Max time to wait for purchase window before moving to next item")]
    [IgnoreMenu]
    public RangeNode<int> TimeoutPerItem { get; set; } = new RangeNode<int>(3, 1, 10);

    // ===== FINE-GRAINED TIMING CONTROLS =====
    [Menu("Mouse Move Delay (ms)", "Delay after moving mouse before clicking (allows tooltip to appear)")]
    [IgnoreMenu]
    public RangeNode<int> MouseMoveDelay { get; set; } = new RangeNode<int>(50, 0, 500);

    [Menu("Post-Click Delay (ms)", "Delay after clicking before checking if purchase succeeded")]
    [IgnoreMenu]
    public RangeNode<int> PostClickDelay { get; set; } = new RangeNode<int>(150, 50, 1000);

    [Menu("Hideout Token Delay (ms)", "Delay after sending hideout token for tab switch")]
    [IgnoreMenu]
    public RangeNode<int> HideoutTokenDelay { get; set; } = new RangeNode<int>(150, 50, 500);

    [Menu("Window Close Check Interval (ms)", "How often to check if old purchase window closed")]
    [IgnoreMenu]
    public RangeNode<int> WindowCloseCheckInterval { get; set; } = new RangeNode<int>(50, 25, 200);

    [Menu("Loading Screen Check Interval (ms)", "How often to check if loading screen finished")]
    [IgnoreMenu]
    public RangeNode<int> LoadingCheckInterval { get; set; } = new RangeNode<int>(100, 50, 500);

    [Menu("Retry Delay (ms)", "Delay between retry attempts when purchase fails")]
    [IgnoreMenu]
    public RangeNode<int> RetryDelay { get; set; } = new RangeNode<int>(300, 100, 1000);

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

    [Menu("Stop After Failed Items", "Stop bulk buying after this many failed items (0 = disabled)")]
    [IgnoreMenu]
    public RangeNode<int> StopAfterFailedItems { get; set; } = new RangeNode<int>(0, 0, 20);

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

    // Grouped submenu properties for BulkBuy (no global purchase limits; limits are per search)
    [Submenu(CollapsedByDefault = true)] public BbGeneralSubMenu General { get; set; }
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
    [Menu("Toggle BulkBuy Hotkey")] public HotkeyNode ToggleHotkey => _p.ToggleHotkey;
}

public class BbTimingDelaysSubMenu
{
    private readonly BulkBuySubSettings _p;
    public BbTimingDelaysSubMenu(BulkBuySubSettings p) { _p = p; }
    
    [Menu("Timing Preset", "Choose preset: Slow (slow PC/load times), Fast (normal), SuperFast (fast PC/load times)")]
    public ListNode TimingPreset => _p.TimingPreset;
    
    [Menu("Timeout Per Item (seconds)", "Max time to wait for purchase window to open before skipping item")] 
    public RangeNode<int> TimeoutPerItem => _p.TimeoutPerItem;
    [Menu("Mouse Move Delay (ms)", "Delay after moving mouse before clicking (allows tooltip to appear). Default: 100ms")] 
    public RangeNode<int> MouseMoveDelay => _p.MouseMoveDelay;
    [Menu("Post-Click Delay (ms)", "Delay after clicking before checking if purchase succeeded. Default: 250ms")] 
    public RangeNode<int> PostClickDelay => _p.PostClickDelay;
    [Menu("Hideout Token Delay (ms)", "Delay after sending hideout token for tab switch. Default: 200ms")] 
    public RangeNode<int> HideoutTokenDelay => _p.HideoutTokenDelay;
    [Menu("Window Close Check Interval (ms)", "How often to check if old purchase window closed. Default: 100ms")] 
    public RangeNode<int> WindowCloseCheckInterval => _p.WindowCloseCheckInterval;
    [Menu("Loading Screen Check Interval (ms)", "How often to check if loading screen finished. Default: 200ms")] 
    public RangeNode<int> LoadingCheckInterval => _p.LoadingCheckInterval;
    [Menu("Retry Delay (ms)", "Delay between retry attempts when purchase fails. Default: 500ms")] 
    public RangeNode<int> RetryDelay => _p.RetryDelay;
}

public class BbSafetySubMenu
{
    private readonly BulkBuySubSettings _p;
    public BbSafetySubMenu(BulkBuySubSettings p) { _p = p; }
    [Menu("Auto-Resume After Rate Limit")] public ToggleNode AutoResumeAfterRateLimit => _p.AutoResumeAfterRateLimit;
    [Menu("Stop on Error")] public ToggleNode StopOnError => _p.StopOnError;
    [Menu("Retry Failed Items")] public ToggleNode RetryFailedItems => _p.RetryFailedItems;
    [Menu("Max Retries Per Item")] public RangeNode<int> MaxRetriesPerItem => _p.MaxRetriesPerItem;
    [Menu("Stop After Failed Items", "Stop bulk buying after this many failed items (0 = disabled)")] 
    public RangeNode<int> StopAfterFailedItems => _p.StopAfterFailedItems;
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
        League = new TextNode("Keepers");
    }

    [Menu("Group Name")]
    public TextNode Name { get; set; }

    [Menu("Enable Group")]
    public ToggleNode Enable { get; set; }

    [Menu("Default League")]
    public TextNode League { get; set; }

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
        QueryJson = new TextNode("");
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

    // Raw JSON query body for non-live trade searches (POST /api/trade/search/Keepers)
    [Menu("Query JSON")]
    public TextNode QueryJson { get; set; }
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
            groupNameBuffer = group.Name.Value;

            bool groupEnabled = group.Enable.Value;
            bool isOpen = ImGui.CollapsingHeader($"Group##bulkgroup{i}");

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && ImGui.GetIO().KeyShift)
            {
                group.Enable.Value = !group.Enable.Value;
                groupEnabled = group.Enable.Value;
            }

            ImGui.SameLine();
            ImGui.Text(groupEnabled ? "[ON]" : "[OFF]");
            ImGui.SameLine();
            ImGui.Text(group.Name.Value);

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
                // Default League for searches in this group
                string groupLeague = group.League?.Value ?? "Keepers";
                if (ImGui.InputText($"League##bulkgroup_league{i}", ref groupLeague, 32))
                {
                    if (group.League == null)
                        group.League = new TextNode("Keepers");
                    group.League.Value = string.IsNullOrWhiteSpace(groupLeague) ? "Keepers" : groupLeague;
                }
                HelpMarker("Default league for new BulkBuy searches in this group (e.g. Keepers). Each search can override its own league.");
                if (ImGui.Button($"Add Search##bulkgroup{i}"))
                {
                    // Create a blank JSON search entry, seeded with the group's default league
                    string newLeague = group.League?.Value ?? "Keepers";
                    group.Searches.Add(new BulkBuySearch
                    {
                        Name = new TextNode($"Search {group.Searches.Count + 1}"),
                        Enable = new ToggleNode(false),
                        League = new TextNode(string.IsNullOrWhiteSpace(newLeague) ? "Keepers" : newLeague),
                        SearchId = new TextNode(""),
                        MaxItems = new RangeNode<int>(10, 1, 100),
                        QueryJson = new TextNode("")
                    });
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
                        bool enableSearch = search.Enable.Value;
                        ImGui.Checkbox($"Enable##bulksearch{i}{j}", ref enableSearch);
                        search.Enable.Value = enableSearch;
                        HelpMarker("Enable or disable this search; right-click header to delete search");

                        // League per search (used to choose /trade/search/{league})
                        string leagueValue = search.League?.Value ?? group.League?.Value ?? "Keepers";
                        if (ImGui.InputText($"League##bulksearch_league{i}{j}", ref leagueValue, 32))
                        {
                            if (search.League == null)
                                search.League = new TextNode("Keepers");
                            search.League.Value = string.IsNullOrWhiteSpace(leagueValue) ? "Keepers" : leagueValue;
                        }
                        HelpMarker("League for this search (e.g. Keepers). Overrides the group's default league.");

                        var maxItems = search.MaxItems.Value;
                        if (ImGui.SliderInt($"Max Items##bulksearch{i}{j}", ref maxItems, 1, 100))
                        {
                            search.MaxItems.Value = maxItems;
                        }
                        HelpMarker("Maximum items to buy from this search");

                        // Query JSON input (multi-line)
                        string queryJson = search.QueryJson?.Value ?? "";
                        if (ImGui.InputTextMultiline(
                                $"Query JSON##bulksearch_query{i}{j}",
                                ref queryJson,
                                4096,
                                new Vector2(0, ImGui.GetTextLineHeight() * 6)))
                        {
                            if (search.QueryJson == null)
                                search.QueryJson = new TextNode("");
                            search.QueryJson.Value = queryJson;
                        }
                        HelpMarker("Paste the full trade search JSON body here (as copied from the browser). League will default to 'Keepers'.");

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
                League = new TextNode("Keepers")
            });
        }
        HelpMarker("Add a new group to organize your bulk buy searches");
    }
}

// ==================== CURRENCY EXCHANGE SUB-PLUGIN SETTINGS ====================
[Submenu(CollapsedByDefault = true)]
public class CurrencyExchangeSubSettings
{
    public CurrencyExchangeSubSettings()
    {
        General = new CeGeneralSubMenu(this);
        PricingStrategy = new CePricingStrategySubMenu(this);
        AutoFill = new CeAutoFillSubMenu(this);
    }

    // ===== MAIN SETTINGS =====
    [Menu("Enable Currency Exchange", "Enable or disable the Currency Exchange sub-plugin")]
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("Debug Mode", "Enable detailed logging for debugging")]
    [IgnoreMenu]
    public ToggleNode DebugMode { get; set; } = new ToggleNode(false);

    [Menu("Show Button", "Display the auto-fill button above input field")]
    [IgnoreMenu]
    public ToggleNode ShowButton { get; set; } = new ToggleNode(true);

    // ===== PRICING STRATEGY =====
    [Menu("Auto Undercut", "Automatically undercut the lowest maker order")]
    [IgnoreMenu]
    public ToggleNode AutoUndercut { get; set; } = new ToggleNode(true);

    [Menu("Undercut Amount", "Amount to undercut by (e.g., 0.01 for 1/100 ratio)")]
    [IgnoreMenu]
    public RangeNode<float> UndercutAmount { get; set; } = new RangeNode<float>(0.01f, 0.001f, 0.1f);


    // ===== AUTO FILL SETTINGS =====
    [Menu("Fill Offered Amount", "Automatically fill the 'I Have' amount with total inventory stock")]
    [IgnoreMenu]
    public ToggleNode FillOfferedAmount { get; set; } = new ToggleNode(true);

    [Menu("Fill Wanted Amount", "Automatically calculate the 'I Want' amount based on best ratio")]
    [IgnoreMenu]
    public ToggleNode FillWantedAmount { get; set; } = new ToggleNode(true);

    [Menu("Auto Click Place Order", "Automatically click 'Place Order' button after filling")]
    [IgnoreMenu]
    public ToggleNode AutoClickPlaceOrder { get; set; } = new ToggleNode(false);

    // ===== ACTION TIMING =====
    [Menu("Action Delay (ms)", "Delay between actions to simulate human behavior")]
    [IgnoreMenu]
    public RangeNode<int> ActionDelay { get; set; } = new RangeNode<int>(75, 10, 500);

    [Menu("Random Delay (ms)", "Random delay added to action delay")]
    [IgnoreMenu]
    public RangeNode<int> RandomDelay { get; set; } = new RangeNode<int>(25, 0, 100);

    // ===== INVENTORY SCANNING =====
    [Menu("Include Stash Tabs", "Include stash tabs when counting inventory")]
    [IgnoreMenu]
    public ToggleNode IncludeStashTabs { get; set; } = new ToggleNode(true);

    [Menu("Include Currency Tab", "Include currency stash tab")]
    [IgnoreMenu]
    public ToggleNode IncludeCurrencyTab { get; set; } = new ToggleNode(true);

    // Grouped submenu properties
    [Submenu(CollapsedByDefault = true)] public CeGeneralSubMenu General { get; set; }
    [Submenu(CollapsedByDefault = true)] public CePricingStrategySubMenu PricingStrategy { get; set; }
    [Submenu(CollapsedByDefault = true)] public CeAutoFillSubMenu AutoFill { get; set; }
}

// ===== CURRENCY EXCHANGE SUBMENU CLASSES =====
public class CeGeneralSubMenu
{
    private readonly CurrencyExchangeSubSettings _p;
    public CeGeneralSubMenu(CurrencyExchangeSubSettings p) { _p = p; }
    [Menu("Debug Mode")] public ToggleNode DebugMode => _p.DebugMode;
    [Menu("Show Button")] public ToggleNode ShowButton => _p.ShowButton;
}

public class CePricingStrategySubMenu
{
    private readonly CurrencyExchangeSubSettings _p;
    public CePricingStrategySubMenu(CurrencyExchangeSubSettings p) { _p = p; }
    [Menu("Auto Undercut")] public ToggleNode AutoUndercut => _p.AutoUndercut;
    [Menu("Undercut Amount")] public RangeNode<float> UndercutAmount => _p.UndercutAmount;
}

public class CeAutoFillSubMenu
{
    private readonly CurrencyExchangeSubSettings _p;
    public CeAutoFillSubMenu(CurrencyExchangeSubSettings p) { _p = p; }
    [Menu("Fill Offered Amount")] public ToggleNode FillOfferedAmount => _p.FillOfferedAmount;
    [Menu("Fill Wanted Amount")] public ToggleNode FillWantedAmount => _p.FillWantedAmount;
    [Menu("Auto Click Place Order")] public ToggleNode AutoClickPlaceOrder => _p.AutoClickPlaceOrder;
    [Menu("Action Delay (ms)")] public RangeNode<int> ActionDelay => _p.ActionDelay;
    [Menu("Random Delay (ms)")] public RangeNode<int> RandomDelay => _p.RandomDelay;
    [Menu("Include Stash Tabs")] public ToggleNode IncludeStashTabs => _p.IncludeStashTabs;
    [Menu("Include Currency Tab")] public ToggleNode IncludeCurrencyTab => _p.IncludeCurrencyTab;
}
