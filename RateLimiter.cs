using System;
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

            // Get safety threshold from settings
            var settings = _getSettings?.Invoke();
            var safetyThreshold = settings?.RateLimiting.RateLimitSafetyThreshold.Value ?? 10;

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
    public void ParseRateLimitHeaders(HttpResponseMessage response)
    {
        try
        {
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
                    var scope = "account";
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
                var scope = "account";
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
            lock (_lock)
            {
                if (_rateLimits.ContainsKey("account"))
                {
                    _rateLimits["account"].Remaining = 0;
                }
            }

            // Parse Retry-After header
            if (response.Headers.TryGetValues("Retry-After", out var retryAfterHeader))
            {
                if (int.TryParse(retryAfterHeader.First(), out var retryAfterSeconds))
                {
                    var waitTime = retryAfterSeconds * 1000;
                    _logMessage($"🚨 RATE LIMITED! Waiting {retryAfterSeconds} seconds before retry...");
                    await Task.Delay(waitTime);
                    return waitTime;
                }
            }
            else
            {
                _logMessage("🚨 RATE LIMITED! No Retry-After header, waiting 60 seconds...");
                await Task.Delay(60000);
                return 60000;
            }
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
