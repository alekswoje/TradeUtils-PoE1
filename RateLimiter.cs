using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace TradeUtils.Utility;

/// <summary>
/// Simple quota-based rate limiter: full speed until quota too low, then block
/// </summary>
public class QuotaGuard
{
    private readonly Dictionary<string, RateLimitState> _rateLimits = new();
    private readonly object _lock = new();
    private readonly Action<string> _logMessage;
    private readonly Action<string> _logError;
    private readonly Func<LiveSearchSubSettings> _getSettings;

    public QuotaGuard(Action<string> logMessage, Action<string> logError, Func<LiveSearchSubSettings> getSettings = null)
    {
        _logMessage = logMessage;
        _logError = logError;
        _getSettings = getSettings;
    }

    public class RateLimitState
    {
        public int Max { get; set; }
        public int Remaining { get; set; }
        public DateTime ResetTime { get; set; }
        public int Period { get; set; }
        public int Penalty { get; set; }
    }

    /// <summary>
    /// Check if we can make a request. Returns true = go ahead, false = quota too low
    /// </summary>
    public bool CanMakeRequest(string scope = "account")
    {
        lock (_lock)
        {
            if (!_rateLimits.ContainsKey(scope))
                return true; // No info yet, allow

            var state = _rateLimits[scope];

            // Check if quota has reset
            if (DateTime.Now > state.ResetTime)
            {
                state.Remaining = state.Max;
                state.ResetTime = DateTime.Now.AddSeconds(state.Period);
                _logMessage($"✅ QUOTA RESET: {state.Remaining}/{state.Max} available");
            }

            // Fixed at 10%. This was a setting, but a rate-limit margin is a safety mechanism
            // rather than a preference — lowering it only ever earns a ban from GGG's API.
            const int safetyThreshold = 10;

            // Calculate how many requests to reserve based on threshold
            // ALWAYS reserve at least 1 request, even if threshold is 0%
            int reservedRequests = Math.Max(1, (int)Math.Ceiling(state.Max * safetyThreshold / 100.0));
            
            if (state.Max <= 0)
                return true; // No limit info

            // Warning if approaching threshold
            if (state.Remaining <= reservedRequests + 1 && state.Remaining > reservedRequests)
            {
                _logMessage($"⚠️ QUOTA LOW: {state.Remaining}/{state.Max} remaining - will block at {reservedRequests} (threshold {safetyThreshold}%)");
            }

            // Allow if we have more than the reserved amount
            // This ensures we ALWAYS keep at least 1 request in reserve
            return state.Remaining > reservedRequests;
        }
    }

    /// <summary>
    /// Parse rate limit headers from API response
    /// </summary>
    /// <summary>
    /// The bucket a response belongs to, taken from GGG's own <c>X-Rate-Limit-Policy</c> header.
    ///
    /// Every trade endpoint has its own policy with wildly different allowances - search is
    /// 5 per 10s with a 60s penalty, fetch is 12 per 4s with a 10s one - so filing them all under a
    /// single bucket makes both numbers wrong. Naming buckets after the policy means the endpoints
    /// separate themselves without anything here having to know what they are.
    /// </summary>
    /// <summary>
    /// Presents an IP-scoped header under the Account name the parser below already understands,
    /// so both forms are read by one code path rather than two that can drift apart.
    /// </summary>
    private static void CopyHeader(HttpResponseMessage response, string from, string to)
    {
        try
        {
            if (response == null || response.Headers.Contains(to)) return;
            if (!response.Headers.TryGetValues(from, out var values)) return;

            response.Headers.TryAddWithoutValidation(to, values);
        }
        catch
        {
            // A header we can't copy just means this response goes untracked.
        }
    }

    public static string ScopeOf(HttpResponseMessage response)
    {
        try
        {
            if (response != null && response.Headers.TryGetValues("X-Rate-Limit-Policy", out var policy))
            {
                var name = policy.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
        }
        catch
        {
            // Fall through to the shared bucket.
        }

        return "account";
    }

    public void ParseRateLimitHeaders(HttpResponseMessage response)
    {
        try
        {
            // GGG rate-limits the trade endpoints by IP and everything else by account, sending
            // X-Rate-Limit-Ip / -Ip-State for the former. Only the Account pair used to be read, so
            // search and fetch contributed nothing and were then gated against whichever bucket the
            // whisper calls had filled in - a limit that had nothing to do with them.
            CopyHeader(response, "X-Rate-Limit-Ip", "X-Rate-Limit-Account");
            CopyHeader(response, "X-Rate-Limit-Ip-State", "X-Rate-Limit-Account-State");

            // Parse X-Rate-Limit-Rules header
            if (response.Headers.TryGetValues("X-Rate-Limit-Rules", out var rulesHeader))
            {
                var rules = string.Join(",", rulesHeader).Split(',');
                foreach (var rule in rules)
                {
                    var parts = rule.Trim().Split(':');
                    if (parts.Length >= 4)
                    {
                        var scope = parts[0];
                        if (int.TryParse(parts[1], out var hits) &&
                            int.TryParse(parts[2], out var period) &&
                            int.TryParse(parts[3], out var penalty))
                        {
                            lock (_lock)
                            {
                                if (!_rateLimits.ContainsKey(scope))
                                {
                                    _rateLimits[scope] = new RateLimitState
                                    {
                                        Max = hits,
                                        Remaining = hits,
                                        Period = period,
                                        Penalty = penalty,
                                        ResetTime = DateTime.Now.AddSeconds(period)
                                    };
                                }
                                else
                                {
                                    _rateLimits[scope].Max = hits;
                                    _rateLimits[scope].Period = period;
                                    _rateLimits[scope].Penalty = penalty;
                                }
                            }
                        }
                    }
                }
            }

            // Parse X-Rate-Limit-Account header - this defines the RULES
            // Format: "max:period:penalty" e.g., "6:4:10" means 6 max hits per 4 seconds, 10s penalty
            // NOTE: GGG sends MULTIPLE windows (e.g., "6:4:10,900:21600:600")
            // We only care about the SHORTEST period (most restrictive), not the long-term hourly limit
            if (response.Headers.TryGetValues("X-Rate-Limit-Account", out var accountHeader))
            {
                var accountData = string.Join(",", accountHeader).Split(',');
                
                // Find the rule with the SHORTEST period (most restrictive short-term limit)
                int shortestPeriod = int.MaxValue;
                int selectedMax = 0;
                int selectedPeriod = 0;
                int selectedPenalty = 0;
                
                foreach (var data in accountData)
                {
                    var parts = data.Trim().Split(':');
                    if (parts.Length >= 3)
                    {
                        if (int.TryParse(parts[0], out var max) &&
                            int.TryParse(parts[1], out var period) &&
                            int.TryParse(parts[2], out var penalty))
                        {
                            // Only use rules with short periods (ignore 6-hour limits etc.)
                            // We want the immediate rate limit, not the long-term one
                            if (period < shortestPeriod && period <= 60) // Only consider limits <= 60 seconds
                            {
                                shortestPeriod = period;
                                selectedMax = max;
                                selectedPeriod = period;
                                selectedPenalty = penalty;
                            }
                        }
                    }
                }
                
                // Apply the shortest-period rule if we found one
                if (shortestPeriod != int.MaxValue)
                {
                    var scope = ScopeOf(response);
                    lock (_lock)
                    {
                        if (_rateLimits.ContainsKey(scope))
                        {
                            _rateLimits[scope].Max = selectedMax;
                            _rateLimits[scope].Period = selectedPeriod;
                            _rateLimits[scope].Penalty = selectedPenalty;
                        }
                        else
                        {
                            _rateLimits[scope] = new RateLimitState
                            {
                                Max = selectedMax,
                                Remaining = selectedMax, // Start with full quota
                                Period = selectedPeriod,
                                Penalty = selectedPenalty,
                                ResetTime = DateTime.Now.AddSeconds(selectedPeriod)
                            };
                        }
                    }
                }
            }

            // Parse X-Rate-Limit-Account-State header - this has the CURRENT STATE
            // Format: "hits:period:restricted" e.g., "2:4:0" means 2 hits used, 4s period, 0s restricted
            // NOTE: GGG sends MULTIPLE states (e.g., "5:4:0,356:21600:0")
            // We only care about the state matching our short-term limit period, not the long-term one
            if (response.Headers.TryGetValues("X-Rate-Limit-Account-State", out var stateHeader))
            {
                var stateData = string.Join(",", stateHeader).Split(',');
                
                // Find the state entry that matches our tracked period (shortest period from rules)
                var scope = ScopeOf(response);
                int trackedPeriod = 0;
                
                lock (_lock)
                {
                    if (_rateLimits.ContainsKey(scope))
                    {
                        trackedPeriod = _rateLimits[scope].Period;
                    }
                }
                
                // Look for the state entry matching our tracked period
                foreach (var data in stateData)
                {
                    var parts = data.Trim().Split(':');
                    if (parts.Length >= 3)
                    {
                        if (int.TryParse(parts[0], out var hits) &&
                            int.TryParse(parts[1], out var period) &&
                            int.TryParse(parts[2], out var restricted))
                        {
                            // Only process state if period matches our tracked period, OR if we have no tracked period yet
                            // Also only consider short-term periods (<= 60 seconds) to avoid hourly limits
                            if (period <= 60 && (trackedPeriod == 0 || period == trackedPeriod))
                            {
                                lock (_lock)
                                {
                                    if (_rateLimits.ContainsKey(scope))
                                    {
                                        // Calculate remaining from max - hits
                                        var max = _rateLimits[scope].Max;
                                        _rateLimits[scope].Remaining = Math.Max(0, max - hits);
                                        
                                        // Update period from state (might have changed)
                                        _rateLimits[scope].Period = period;
                                        
                                        // If restricted > 0, we're actively rate limited - use restricted time
                                        if (restricted > 0)
                                        {
                                            _rateLimits[scope].Remaining = 0;
                                            _rateLimits[scope].ResetTime = DateTime.Now.AddSeconds(restricted);
                                            _logMessage($"⏱️ Rate limited! Quota resets in {restricted} seconds");
                                        }
                                        else
                                        {
                                            // Not restricted - quota resets in 'period' seconds
                                            _rateLimits[scope].ResetTime = DateTime.Now.AddSeconds(period);
                                        }
                                    }
                                    else
                                    {
                                        // No rules yet, create with default values (only for short periods)
                                        _rateLimits[scope] = new RateLimitState
                                        {
                                            Max = 100, // Default, will be updated by rules header
                                            Remaining = Math.Max(0, 100 - hits),
                                            Period = period,
                                            ResetTime = DateTime.Now.AddSeconds(period)
                                        };
                                    }
                                }
                                break; // Found matching state, stop looking
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logError($"Error parsing rate limit headers: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle 429 Too Many Requests response
    /// </summary>
    public async Task<int> HandleRateLimitResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _logMessage($"🚨 RATE LIMITED! Got 429 response");

            // Force remaining to 0
            var limitedScope = ScopeOf(response);
            lock (_lock)
            {
                if (_rateLimits.ContainsKey(limitedScope))
                {
                    _rateLimits[limitedScope].Remaining = 0;
                }
            }

            // Back off. A 429 ALWAYS waits, whatever the header looks like.
            //
            // This used to have a hole big enough to drive a request storm through: a Retry-After
            // that was present but unparseable — Cloudflare sends an HTTP-date, not seconds —
            // matched the outer `if`, failed the inner parse, and fell out of both branches without
            // waiting at all. The caller retried immediately and the plugin hammered a service that
            // was already telling it to stop.
            int waitMs = 60_000;
            string why = "no usable Retry-After";

            if (response.Headers.TryGetValues("Retry-After", out var retryAfterHeader))
            {
                var raw = retryAfterHeader.FirstOrDefault()?.Trim() ?? "";

                if (int.TryParse(raw, out var seconds) && seconds >= 0)
                {
                    waitMs = seconds * 1000;
                    why = $"Retry-After {seconds}s";
                }
                else if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                             DateTimeStyles.AdjustToUniversal, out var until))
                {
                    // HTTP-date form, which is what a Cloudflare challenge returns.
                    waitMs = (int)Math.Max(0, (until - DateTimeOffset.UtcNow).TotalMilliseconds);
                    why = $"Retry-After {until:HH:mm:ss}Z";
                }
            }

            // Never zero — a 429 answered instantly is the case that produced the storm — and
            // capped so a wild value can't wedge the plugin for hours.
            waitMs = Math.Min(Math.Max(waitMs, 5_000), 300_000);

            _logMessage($"🚨 RATE LIMITED ({limitedScope}) — waiting {waitMs / 1000}s ({why}).");
            await Task.Delay(waitMs);
            return waitMs;
        }

        // Parse rate limit headers for future requests
        ParseRateLimitHeaders(response);
        return 0;
    }

    /// <summary>
    /// Get time until quota resets
    /// </summary>
    public int GetTimeUntilReset(string scope = "account")
    {
        lock (_lock)
        {
            if (!_rateLimits.ContainsKey(scope))
                return 0;

            var timeUntil = (_rateLimits[scope].ResetTime - DateTime.Now).TotalMilliseconds;
            return Math.Max(0, (int)timeUntil);
        }
    }

    /// <summary>
    /// Get current status for logging
    /// </summary>
    public string GetStatus(string scope = "account")
    {
        lock (_lock)
        {
            if (!_rateLimits.ContainsKey(scope))
                return "No rate limit info";

            var state = _rateLimits[scope];
            double remainingPercent = (state.Remaining / (double)state.Max) * 100.0;
            return $"{state.Remaining}/{state.Max} remaining ({remainingPercent:F1}%) - Resets in {GetTimeUntilReset(scope) / 1000}s";
        }
    }

    /// <summary>
    /// Check if currently rate limited (remaining = 0)
    /// </summary>
    public bool IsRateLimited(string scope = "account")
    {
        lock (_lock)
        {
            if (!_rateLimits.ContainsKey(scope))
                return false;

            return _rateLimits[scope].Remaining <= 0;
        }
    }
}
