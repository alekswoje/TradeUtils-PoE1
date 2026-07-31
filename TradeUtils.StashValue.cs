using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TradeUtils.Utility;
using RectangleF = SharpDX.RectangleF;

namespace TradeUtils;

/// <summary>
/// Values every priced item across ALL stash tabs, not just the open one.
///
/// Why this exists: item prices are NOT in game memory. The client only materialises a price into
/// an Element tooltip when you hover the item (Tooltip.Address is 0 until then), and tabs you
/// haven't opened aren't loaded at all — ServerStashTab carries a name and index but no items. So
/// the in-game value display can only ever see what you've manually hovered in the open tab.
///
/// GGG's own character-window endpoint returns every tab's items with their price note server-side,
/// which removes both limits at once. It is authenticated with the same POESESSID the plugin
/// already uses, and rate-limited under the separate "backend-item-request-limit" policy
/// (30/60s, 90/1800s, 180/7200s per IP), so scanning is on-demand and paced rather than continuous.
/// </summary>
public partial class TradeUtils
{
    private const string StashApiBase = "https://www.pathofexile.com/character-window/get-stash-items";
    private const string ProfileApiUrl = "https://www.pathofexile.com/api/profile";
    private const string TradeStaticUrl = "https://www.pathofexile.com/api/trade/data/static";

    // Conservative floor between tab requests. The tightest bucket is 30 requests per 60s, which is
    // exactly 2.0s each; 2.5s leaves headroom because this IP's budget is shared with any trade
    // site open in a browser.
    private static readonly TimeSpan StashScanRequestSpacing = TimeSpan.FromMilliseconds(2500);

    private readonly HttpClient _stashValueHttpClient = new HttpClient();
    private string _stashValueAccountName;

    // Trade-site currency id -> display name ("chaos" -> "Chaos Orb"). Price notes carry the id,
    // while poe.ninja prices by display name, so this bridges the two.
    private Dictionary<string, string> _stashValueCurrencyNames;

    private readonly object _stashValueLock = new object();
    private List<StashTabValue> _stashValueResults = new List<StashTabValue>();
    private DateTime _stashValueLastScan = DateTime.MinValue;
    private string _stashValueStatus;
    private int _stashValueScanning; // 0 = idle, 1 = a scan is running

    /// <summary>Price notes look like "~price 104 chaos" or "~b/o 1.5 divine".</summary>
    private static readonly Regex StashNoteRegex = new Regex(
        @"^\s*~(?:price|b/o|gb/o)\s+(\d+(?:[.,]\d+)?)\s+(\S+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    partial void InitializeStashValue()
    {
        _stashValueStatus = "Not scanned yet.";
    }

    partial void RenderStashValue()
    {
        if (!LowerPriceSettings.Enable.Value) return;

        if (LowerPriceSettings.StashScanHotkey.PressedOnce())
            _ = Task.Run(ScanAllStashTabsAsync);

        if (LowerPriceSettings.ShowStashValueDisplay.Value)
            RenderStashValueDisplay();
    }

    private async Task ScanAllStashTabsAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _stashValueScanning, 1) == 1)
        {
            LogMessage("Stash value: a scan is already running.");
            return;
        }

        try
        {
            var sessionId = EncryptedSettings.GetSecureSessionId();
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                SetStashStatus("POESESSID is not set — configure it in settings.");
                return;
            }

            var league = ResolveLeague();
            SetStashStatus($"Scanning '{league}'…");

            var account = await EnsureStashAccountNameAsync(sessionId);
            if (string.IsNullOrWhiteSpace(account))
            {
                SetStashStatus("Could not resolve your account name (is the POESESSID still valid?).");
                return;
            }

            await EnsureStashCurrencyNamesAsync();
            // Prices come from the same poe.ninja table the open-tab display uses.
            await UpdateLowerPriceCurrencyRates();

            // tabs=1 returns the tab list alongside tab 0's items, so the listing costs no extra request.
            var first = await FetchStashTabAsync(sessionId, account, league, 0, includeTabs: true);
            if (first == null)
            {
                SetStashStatus("Stash request failed — see the log.");
                return;
            }

            var tabs = ReadTabList(first.Value);
            if (tabs.Count == 0)
            {
                SetStashStatus("No stash tabs returned.");
                return;
            }

            var results = new List<StashTabValue>();
            AddTabIfPriced(results, tabs, 0, first.Value);

            for (var i = 1; i < tabs.Count; i++)
            {
                if (tabs[i].IsFolder) continue;

                SetStashStatus($"Scanning tab {i + 1}/{tabs.Count} ('{tabs[i].Name}')…");
                await Task.Delay(StashScanRequestSpacing);

                var doc = await FetchStashTabAsync(sessionId, account, league, tabs[i].Index, includeTabs: false);
                if (doc == null)
                {
                    // FetchStashTabAsync already logged the reason; keep whatever we have.
                    SetStashStatus($"Stopped at tab {i + 1}/{tabs.Count} — see the log. Showing partial totals.");
                    break;
                }

                AddTabIfPriced(results, tabs, i, doc.Value);
            }

            lock (_stashValueLock)
            {
                _stashValueResults = results;
                _stashValueLastScan = DateTime.Now;
            }

            var grand = results.Sum(r => r.ChaosTotal);
            SetStashStatus($"{results.Count} priced tab(s), {results.Sum(r => r.ItemsPriced)} item(s).");
            LogMessage($"Stash value: scanned {tabs.Count} tab(s) in '{league}'; {results.Count} contained priced items, total {grand:N0} chaos.");
        }
        catch (Exception ex)
        {
            SetStashStatus($"Scan failed: {ex.Message}");
            LogError($"Stash value scan failed: {ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _stashValueScanning, 0);
        }
    }

    private void AddTabIfPriced(List<StashTabValue> results, List<StashTabInfo> tabs, int listIndex, JsonElement doc)
    {
        var tab = ValueTab(doc);
        if (tab.ItemsPriced == 0) return; // "only tabs with items priced"
        tab.Index = tabs[listIndex].Index;
        tab.Name = tabs[listIndex].Name;
        results.Add(tab);
    }

    /// <summary>Sums the price notes in one stash payload.</summary>
    private StashTabValue ValueTab(JsonElement doc)
    {
        var tab = new StashTabValue();
        if (!doc.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return tab;

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("note", out var noteEl)) continue;
            var note = noteEl.GetString();
            if (string.IsNullOrWhiteSpace(note)) continue;

            var m = StashNoteRegex.Match(note);
            if (!m.Success) continue;

            if (!decimal.TryParse(m.Groups[1].Value.Replace(',', '.'),
                                  NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                continue;
            if (amount <= 0) continue;

            var slug = m.Groups[2].Value;
            var display = ResolveStashCurrencyName(slug);

            tab.ItemsPriced++;
            tab.OrbTotals.TryGetValue(display, out var soFar);
            tab.OrbTotals[display] = soFar + amount;

            var chaosEach = GetLowerPriceChaosValue(display);
            if (chaosEach > 0)
                tab.ChaosTotal += amount * chaosEach;
            else
                tab.UnpricedItems++;
        }

        return tab;
    }

    private string ResolveStashCurrencyName(string slug)
    {
        var map = _stashValueCurrencyNames;
        if (map != null && map.TryGetValue(slug, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;
        return slug; // Unknown id: keep it visible in the breakdown rather than dropping the item.
    }

    private async Task<string> EnsureStashAccountNameAsync(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(_stashValueAccountName))
            return _stashValueAccountName;

        try
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, ProfileApiUrl))
            {
                req.Headers.Add("User-Agent", PluginUserAgent);
                req.Headers.Add("Cookie", $"POESESSID={sessionId}");
                using (var resp = await _stashValueHttpClient.SendAsync(req))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        LogError($"Stash value: /api/profile returned HTTP {(int)resp.StatusCode}. A 401 means the POESESSID has expired.");
                        return null;
                    }

                    var json = await resp.Content.ReadAsStringAsync();
                    using (var doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("name", out var nameEl))
                            _stashValueAccountName = nameEl.GetString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"Stash value: could not read your account name ({ex.Message}).");
        }

        return _stashValueAccountName;
    }

    private async Task EnsureStashCurrencyNamesAsync()
    {
        if (_stashValueCurrencyNames != null) return;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, TradeStaticUrl))
            {
                req.Headers.Add("User-Agent", PluginUserAgent);
                using (var resp = await _stashValueHttpClient.SendAsync(req))
                {
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync();
                        using (var doc = JsonDocument.Parse(json))
                        {
                            if (doc.RootElement.TryGetProperty("result", out var groups))
                            {
                                foreach (var group in groups.EnumerateArray())
                                {
                                    if (!group.TryGetProperty("entries", out var entries)) continue;
                                    foreach (var e in entries.EnumerateArray())
                                    {
                                        if (e.TryGetProperty("id", out var idEl) &&
                                            e.TryGetProperty("text", out var textEl))
                                        {
                                            var id = idEl.GetString();
                                            var text = textEl.GetString();
                                            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(text))
                                                map[id] = text;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        LogMessage($"Stash value: trade static data returned HTTP {(int)resp.StatusCode}; currency names will show as raw ids.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"Stash value: could not load trade static data ({ex.Message}); currency names will show as raw ids.");
        }

        _stashValueCurrencyNames = map;
    }

    /// <summary>One tab request. Returns null on failure (already logged). Honours 429 Retry-After once.</summary>
    private async Task<JsonElement?> FetchStashTabAsync(string sessionId, string account, string league, int tabIndex, bool includeTabs)
    {
        var url = $"{StashApiBase}?accountName={Uri.EscapeDataString(account)}&realm=pc" +
                  $"&league={Uri.EscapeDataString(league)}&tabIndex={tabIndex}&tabs={(includeTabs ? 1 : 0)}";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    req.Headers.Add("User-Agent", PluginUserAgent);
                    req.Headers.Add("Cookie", $"POESESSID={sessionId}");
                    using (var resp = await _stashValueHttpClient.SendAsync(req))
                    {
                        if (resp.StatusCode == (HttpStatusCode)429)
                        {
                            var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
                            if (attempt == 0)
                            {
                                LogMessage($"Stash value: rate limited, waiting {wait.TotalSeconds:F0}s before retrying tab {tabIndex}.");
                                await Task.Delay(wait);
                                continue;
                            }

                            LogError($"Stash value: still rate limited on tab {tabIndex}; stopping so the penalty doesn't escalate.");
                            return null;
                        }

                        if (!resp.IsSuccessStatusCode)
                        {
                            LogError($"Stash value: tab {tabIndex} returned HTTP {(int)resp.StatusCode}." +
                                     (resp.StatusCode == HttpStatusCode.Forbidden
                                         ? " A 403 usually means the POESESSID is expired or the league name is wrong."
                                         : string.Empty));
                            return null;
                        }

                        var json = await resp.Content.ReadAsStringAsync();
                        using (var doc = JsonDocument.Parse(json))
                            return doc.RootElement.Clone(); // Clone: the document is disposed here.
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Stash value: tab {tabIndex} request failed ({ex.Message}).");
                return null;
            }
        }

        return null;
    }

    private static List<StashTabInfo> ReadTabList(JsonElement doc)
    {
        var list = new List<StashTabInfo>();
        if (!doc.TryGetProperty("tabs", out var tabs) || tabs.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var t in tabs.EnumerateArray())
        {
            var info = new StashTabInfo
            {
                Index = t.TryGetProperty("i", out var iEl) && iEl.TryGetInt32(out var i) ? i : list.Count,
                Name = t.TryGetProperty("n", out var nEl) ? nEl.GetString() : $"Tab {list.Count}",
                IsFolder = t.TryGetProperty("type", out var tyEl) &&
                           string.Equals(tyEl.GetString(), "Folder", StringComparison.OrdinalIgnoreCase),
            };
            list.Add(info);
        }

        return list;
    }

    private void SetStashStatus(string status)
    {
        lock (_stashValueLock) _stashValueStatus = status;
    }

    /// <summary>
    /// The last scanned value for the tab currently open in the merchant panel, or null if it
    /// hasn't been scanned. Matching is by tab NAME rather than index: the API's tab index counts
    /// every tab, while IndexVisibleStash counts only visible ones, so the two diverge as soon as
    /// a tab is hidden or nested in a folder.
    /// </summary>
    private StashTabValue GetScannedValueForOpenTab()
    {
        try
        {
            List<StashTabValue> results;
            lock (_stashValueLock) results = _stashValueResults;
            if (results == null || results.Count == 0) return null;

            var panel = GameController?.IngameState?.IngameUi?.OfflineMerchantPanel;
            if (panel == null) return null;

            var visibleIndex = panel.IndexVisibleStash;
            var serverTabs = GameController?.IngameState?.ServerData?.PlayerStashTabs;
            if (serverTabs == null) return null;

            string openName = null;
            foreach (var t in serverTabs)
            {
                if (t == null) continue;
                if (t.VisibleIndex == visibleIndex) { openName = t.Name; break; }
            }

            if (string.IsNullOrWhiteSpace(openName)) return null;

            return results.FirstOrDefault(r => string.Equals(r.Name, openName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null; // Never let the overlay throw over a display nicety.
        }
    }

    private void RenderStashValueDisplay()
    {
        try
        {
            List<StashTabValue> results;
            string status;
            DateTime lastScan;
            lock (_stashValueLock)
            {
                results = _stashValueResults;
                status = _stashValueStatus;
                lastScan = _stashValueLastScan;
            }

            var scanning = _stashValueScanning == 1;
            if (!scanning && results.Count == 0 && lastScan == DateTime.MinValue)
                return; // Nothing to show until the first scan.

            var divineInChaos = GetLowerPriceChaosValue("Divine Orb");
            var text = "All tabs\n";

            foreach (var tab in results.OrderByDescending(r => r.ChaosTotal))
            {
                text += $"{tab.Name}: {tab.ChaosTotal:N0}c";
                if (divineInChaos > 0)
                    text += $" ({tab.ChaosTotal / divineInChaos:F1}d)";
                text += $"  [{tab.ItemsPriced}]\n";
            }

            var grandChaos = results.Sum(r => r.ChaosTotal);
            text += $"\nTotal in Chaos: {grandChaos:N0}\n";
            text += divineInChaos > 0
                ? $"Total in Divine: {grandChaos / divineInChaos:F2}"
                : "Total in Divine: rates unavailable";

            var unpriced = results.Sum(r => r.UnpricedItems);
            if (unpriced > 0)
                text += $"\n({unpriced} item(s) in an unpriced currency)";

            if (!string.IsNullOrWhiteSpace(status))
                text += $"\n{status}";
            if (!scanning && lastScan != DateTime.MinValue)
                text += $"\nScanned {lastScan:HH:mm}";

            var pos = new Vector2(LowerPriceSettings.StashValueDisplayX.Value,
                                  LowerPriceSettings.StashValueDisplayY.Value);
            var size = Graphics.MeasureText(text);
            Graphics.DrawBox(new RectangleF(pos.X - 5, pos.Y - 5, size.X + 10, size.Y + 10),
                             new SharpDX.Color(0, 0, 0, 180));
            Graphics.DrawText(text, pos);
        }
        catch (Exception ex)
        {
            LogError($"Error rendering stash value display: {ex.Message}");
        }
    }
}

public class StashTabInfo
{
    public int Index { get; set; }
    public string Name { get; set; }
    public bool IsFolder { get; set; }
}

public class StashTabValue
{
    public int Index { get; set; }
    public string Name { get; set; }
    public int ItemsPriced { get; set; }
    public int UnpricedItems { get; set; }
    public decimal ChaosTotal { get; set; }

    /// <summary>Sum of asking prices per orb type, e.g. "Divine Orb" -> 12.</summary>
    public Dictionary<string, decimal> OrbTotals { get; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
}
