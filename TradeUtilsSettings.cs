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

    [Menu("Auto-Detect League", "Use the league your character is currently in for trade searches and currency rates. Leave this ON so searches keep working after every league launch (fixes the 'Invalid query' error from the old hardcoded league). Turn OFF only if you want to force a manually-set league per search.")]
    public ToggleNode AutoDetectLeague { get; set; } = new ToggleNode(true);

    // The client does not expose the league in memory (ServerData.League reads empty even in-world),
    // so auto-detection has to ask GGG which league the character is in, which needs a working
    // POESESSID. When that can't answer, repricing refuses rather than guess - and without this
    // field there was no way to tell it otherwise.
    [Menu("League Override", "Leave empty to auto-detect. Type a league name (e.g. Standard) if the value display says the league is unconfirmed, or if repricing has stopped working. This is treated as the real answer, so make sure it matches the character you're playing.")]
    public TextNode LeagueOverride { get; set; } = new TextNode("");

    // One delay for every sub-plugin that drives the UI, rather than a separate pair each. A random
    // third is added on top automatically, so there's no separate jitter slider any more.
    [Menu("Action Delay (ms)", "Pause between UI actions, so input doesn't arrive faster than the client draws")]
    public RangeNode<int> ActionDelay { get; set; } = new RangeNode<int>(75, 10, 1000);

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
    public AutoFeaturesSubMenu AutoFeatures { get; set; } = new AutoFeaturesSubMenu();

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

                            // Space the tabs out so the browser doesn't drop any of them.
                            System.Threading.Thread.Sleep(5 * 1000);
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

    // The one debug switch for the whole plugin — there used to be four, one per sub-plugin.
    [Menu("Debug Mode", "Verbose logging across every part of the plugin")]
    public ToggleNode DebugMode { get; set; } = new ToggleNode(false);

    [Menu("Travel Hotkey", "Teleport to the most recent search result")]
    public HotkeyNode TravelHotkey { get; set; } = new HotkeyNode(Keys.None);

    // Also stops bulk buy; that had its own duplicate hotkey before.
    [Menu("Stop All Hotkey", "Stop every running search and any bulk buy in progress")]
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
public class AutoFeaturesSubMenu
{
    [Menu("Auto Teleport", "Automatically teleport to items")]
    public ToggleNode AutoTp { get; set; } = new ToggleNode(false);

    [Menu("Auto Buy", "Automatically Ctrl+Left Click after moving mouse to item")]
    public ToggleNode AutoBuy { get; set; } = new ToggleNode(false);

    [Menu("Auto Stash", "Automatically stash items when inventory is full")]
    public ToggleNode AutoStash { get; set; } = new ToggleNode(false);

    [Menu("Fast Mode", "Bypass window checks and directly click after teleport")]
    public ToggleNode FastMode { get; set; } = new ToggleNode(false);
}

// ==================== LOWERPRICE SUB-PLUGIN SETTINGS ====================
[Submenu(CollapsedByDefault = true)]
public class LowerPriceSubSettings
{
    [Menu("Enable LowerPrice", "Enable or disable the LowerPrice sub-plugin")]
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    // ===== WHAT TO REPRICE =====
    // Chaos, Exalted and Annulment listings are always repriced. Divine and Mirror get their own
    // opt-out because a mistake on those is worth the most.
    [Menu("Reprice Divine Orb listings", "Turn off to leave Divine-priced items alone")]
    public ToggleNode RepriceDivine { get; set; } = new ToggleNode(true);

    [Menu("Reprice Mirror of Kalandra listings", "Off by default — an automated mistake costs the most here")]
    public ToggleNode RepriceMirror { get; set; } = new ToggleNode(false);

    // ===== PRICING STRATEGY =====
    [Menu("Use Flat Reduction", "Subtract a fixed amount each run instead of multiplying by the ratio")]
    public ToggleNode UseFlatReduction { get; set; } = new ToggleNode(false);

    [Menu("Price Ratio", "Multiplier applied to the price each run (0.9 = drop it by 10%)")]
    public RangeNode<float> PriceRatio { get; set; } = new RangeNode<float>(0.9f, 0.0f, 1.0f);

    [Menu("Flat Reduction Amount", "Amount subtracted each run when Use Flat Reduction is on")]
    public RangeNode<int> FlatReductionAmount { get; set; } = new RangeNode<int>(1, 1, 100);

    // Per-currency opt-in to flat reduction, for the two tiers where a percentage is a huge step:
    // 10% off 38 Divine is nearly 4 Divine gone in one run, where a flat 1 eases it down. Only these
    // two exist on purpose - the old build had five of these and three were named *UseRatio while
    // forcing flat, which is how a Chaos listing quietly ignored the Price Ratio slider entirely.
    [Menu("Divine: always flat", "Divine-priced listings drop by the Flat Reduction Amount instead of the Price Ratio, whatever the global setting says.")]
    public ToggleNode DivineUseFlat { get; set; } = new ToggleNode(true);

    [Menu("Mirror: always flat", "Mirror-priced listings drop by the Flat Reduction Amount instead of the Price Ratio, whatever the global setting says.")]
    public ToggleNode MirrorUseFlat { get; set; } = new ToggleNode(true);

    // ===== AT THE BOTTOM OF THE RANGE =====
    [Menu("Step Down Currency", "Once a price gets low, every further cut is enormous — 3 Divine to 2 is 33%, 2 to 1 is 50%, and 1 can't be lowered at all. With this on, a listing at or below the threshold is relisted in the next cheaper currency at the poe.ninja equivalent (Mirror to Divine to Chaos), so it keeps stepping down in fine increments instead. Needs poe.ninja rates.")]
    public ToggleNode StepDownCurrency { get; set; } = new ToggleNode(false);

    [Menu("Step Down At or Below", "Step to the cheaper currency once the price is this low. At 3, a listing at 3, 2 or 1 Divine converts to Chaos rather than taking a 33-50% cut.")]
    public RangeNode<int> StepDownAtOrBelow { get; set; } = new RangeNode<int>(3, 1, 50);

    [Menu("Pickup Items at 1 Currency", "Control-left-click items priced at 1 instead of repricing. Ignored when Step Down Currency handles the item first.")]
    public ToggleNode PickupItemsAtOne { get; set; } = new ToggleNode(false);

    // ===== HOTKEYS & DISPLAY =====
    [Menu("Reprice Hotkey", "Hotkey to trigger repricing manually")]
    public HotkeyNode ManualRepriceHotkey { get; set; } = new HotkeyNode(Keys.None);

    [Menu("Scan All Tabs Hotkey", "Fetches every stash tab's prices from GGG's API. Rate limited, so use sparingly.")]
    public HotkeyNode StashScanHotkey { get; set; } = new HotkeyNode(System.Windows.Forms.Keys.F6);

    [Menu("Show Value Display", "Display the value of items in the merchant panel, and the last all-tabs scan")]
    public ToggleNode ShowValueDisplay { get; set; } = new ToggleNode(true);

    [Menu("Enable Reprice Timer", "Play a sound and show a countdown when it's time to reprice again")]
    public ToggleNode EnableTimer { get; set; } = new ToggleNode(false);
}

// ==================== BULKBUY SUB-PLUGIN SETTINGS ====================
[Submenu(CollapsedByDefault = true)]
public class BulkBuySubSettings
{
    public BulkBuySubSettings()
    {
        GroupsConfig = new BulkBuyGroupsRenderer(this);

        // Initialize timing preset values (Fast preset by default)
        TimingPreset = new ListNode();
        TimingPreset.Value = "Fast"; // Default to Fast preset
    }

    // ===== MAIN SETTINGS =====
    [Menu("Enable BulkBuy", "Enable or disable the BulkBuy sub-plugin")]
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("Toggle BulkBuy Hotkey", "Key to start/stop bulk buying")]
    public HotkeyNode ToggleHotkey { get; set; } = new HotkeyNode(Keys.None);

    [Menu("Timing Preset", "Choose preset: Slow (slow PC/load times), Fast (normal), SuperFast (fast PC/load times)")]
    public ListNode TimingPreset { get; set; } = new ListNode(); // Default to "Fast" (index 1)

    [Menu("Stop on Error", "Stop bulk buying if an error occurs, instead of retrying and carrying on")]
    public ToggleNode StopOnError { get; set; } = new ToggleNode(false);

    // ===== PRESET-DRIVEN TIMING =====
    // Not settings any more — ApplyTimingPreset() writes all of these, so exposing them as sliders
    // alongside the preset that overwrites them was only ever a way to confuse people.
    [IgnoreMenu] public RangeNode<int> TimeoutPerItem { get; set; } = new RangeNode<int>(3, 1, 10);
    [IgnoreMenu] public RangeNode<int> MouseMoveDelay { get; set; } = new RangeNode<int>(50, 0, 500);
    [IgnoreMenu] public RangeNode<int> PostClickDelay { get; set; } = new RangeNode<int>(150, 50, 1000);
    [IgnoreMenu] public RangeNode<int> HideoutTokenDelay { get; set; } = new RangeNode<int>(150, 50, 500);
    [IgnoreMenu] public RangeNode<int> WindowCloseCheckInterval { get; set; } = new RangeNode<int>(50, 25, 200);
    [IgnoreMenu] public RangeNode<int> LoadingCheckInterval { get; set; } = new RangeNode<int>(100, 50, 500);
    [IgnoreMenu] public RangeNode<int> RetryDelay { get; set; } = new RangeNode<int>(300, 100, 1000);

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
    // ===== MAIN SETTINGS =====
    [Menu("Enable Currency Exchange", "Enable or disable the Currency Exchange sub-plugin")]
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("Auto Undercut", "Undercut the lowest maker order when filling in a ratio")]
    public ToggleNode AutoUndercut { get; set; } = new ToggleNode(true);

    [Menu("Auto Click Place Order", "Automatically click 'Place Order' after filling the form in")]
    public ToggleNode AutoClickPlaceOrder { get; set; } = new ToggleNode(false);
}

