# TradeUtils - Path of Exile 1 Trade Plugin

A comprehensive Path of Exile 1 plugin for ExileCore that provides three powerful trading features: LiveSearch, LowerPrice, and BulkBuy.

**This is a port of the POE2 TradeUtils plugin adapted for POE1 (ExileCore).**

## Features

### LiveSearch
Connect to multiple live trade searches simultaneously with automatic teleportation and item tracking.

- Real-time trade search monitoring via WebSocket
- Auto-teleport to seller hideouts
- Smart mouse movement to items in purchase window
- Sound alerts for new items
- Rate limiting protection
- Secure POESESSID storage (Windows Credential Manager)
- Fast mode for competitive purchases
- Auto-stash when inventory is full
- Multi-search group management

### LowerPrice
Automatically adjust prices on items in your trade stash.

- Bulk repricing with configurable strategies
- Support for Chaos, Divine, Exalted, and Annulment orbs
- Percentage or flat reduction pricing models
- Currency-specific overrides
- Timer with sound notifications
- Value display showing total worth in multiple currencies
- Auto-update currency rates from poe.ninja
- Special handling for 1-currency items

### BulkBuy (In Development)
Purchase multiple items from trade searches automatically.

**Status:** Basic structure implemented, full automation features pending.

- Queue-based bulk purchasing system (pending)
- Configurable delays for human-like behavior (pending)
- Auto-retry failed purchases (pending)
- Rate limit handling with auto-resume (pending)
- Purchase logging to CSV (pending)
- Progress tracking and statistics (GUI ready)
- Emergency stop hotkey (implemented)
- Multi-search support with per-search limits (settings ready)

## Setup

### Session ID
1. Go to pathofexile.com and log in
2. Press F12 to open Developer Tools
3. Go to Application/Storage > Cookies > pathofexile.com
4. Copy the POESESSID value (32 characters)
5. Paste it in the plugin settings

### LiveSearch
1. Create search groups in settings
2. Add searches by pasting trade URLs from pathofexile.com/trade
3. Enable the searches you want to track
4. Configure hotkeys and preferences

### LowerPrice
1. Enable LowerPrice in settings
2. Configure pricing strategy (percentage or flat reduction)
3. Select which currencies to reprice
4. Set currency-specific overrides if needed
5. Optionally enable timer and value display

### BulkBuy
1. Enable BulkBuy in settings
2. Create groups and add trade search URLs
3. Configure delays and safety limits
4. (Full automation pending - manual implementation required)
4. Set max items to buy per search
5. Click "Start Bulk Buy" when ready

## Configuration

### LiveSearch Settings
- Session ID: POESESSID for authentication
- Travel Hotkey: Manual teleport trigger
- Stop All Hotkey: Emergency stop
- Auto TP: Automatic teleportation
- Fast Mode: Rapid clicking for competitive buying
- Rate Limit Safety: API quota protection

### LowerPrice Settings
- Action Delay: Time between repricing actions
- Currency Selection: Choose which orbs to reprice
- Pricing Strategy: Percentage multiplier or flat reduction
- Timer: Notification after X minutes
- Value Display: Show total stash value

### BulkBuy Settings
- Max Items to Buy: Purchase limit (0 = unlimited)
- Delay Between Purchases: Human-like behavior timing
- Timeout Per Item: Max wait for purchase window
- Auto-Resume: Continue after rate limit cooldown
- Retry Failed Items: Attempt failed purchases again

## API Endpoints (POE1)

- Trade Search: `https://www.pathofexile.com/trade/search/{league}/{searchId}`
- Live Updates: `wss://www.pathofexile.com/api/trade/live/{league}/{searchId}`
- Fetch Items: `https://www.pathofexile.com/api/trade/fetch/{ids}?query={searchId}&realm=pc`

## Safety Features

- Built-in rate limiting to prevent API bans
- Burst protection for item processing
- Secure credential storage
- Emergency stop hotkeys
- Human-like timing with randomization
- Auto-pause on rate limit
- Purchase logging for transparency

## Credits

- Ported from TradeUtils (POE2 version)
- Built for ExileCore (POE1)
- Original inspiration from various POE trading tools

## License

See LICENSE file for details.
