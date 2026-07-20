# TradeUtils - Path of Exile 1 Trade Plugin

A Path of Exile 1 plugin for ExileCore providing three trading features: LiveSearch, LowerPrice, and BulkBuy.

**This is a port of the POE2 TradeUtils plugin adapted for POE1 (ExileCore).**

---

## ⚠️ Account risk — read this before using

**This plugin can get your Path of Exile account banned. Do not run it on an account you are not prepared to lose.**

This is not a hypothetical disclaimer. The plugin does two things that GGG explicitly prohibits:

1. **It reads the game's memory** (through ExileCore/PoEHUD). GGG's own [developer documentation](https://www.pathofexile.com/developer/docs/index#policy) classifies applications that "interact with the game or game files" as strictly against the Terms of Use, and states: *"Creation or use of these type of applications will result in immediate account termination."*

2. **It can automate trade actions** — LiveSearch can automatically whisper sellers and teleport to their hideouts, and FastMode / BulkBuy drive synthetic mouse and keyboard input. GGG's macro policy requires that actions be *"invoked manually by the user"* and perform *"only one action"* per input. Automated whisper/teleport violates both.

When a player asked GGG directly whether a live-search tool that "instantly messages when a listing pops up" was allowed, a GGG web developer replied ([forum thread 3918367](https://www.pathofexile.com/forum/view-thread/3918367), March 2026):

> "Automation is against the terms of use."

The developers of Path of Building were told by GGG that programmatically driving the whisper/teleport endpoints *"will get you banned for emulating the packets sent via the website."* They removed that feature rather than ship it. This plugin still contains it.

### What "banned" actually looks like

Enforcement in this space falls into two buckets:

- **Trade-site locks** (issued by GGG's web team) — usually reversible account locks for using tools against the trade site/API. The instruction is typically "disable the tool." This is the lower-severity outcome.
- **"Third-Party Software" bans** (issued by anti-cheat) — permanent account bans. This is where memory-reading tools and input automation land, and this is the outcome that does **not** come back.

There is no public evidence of a mass "trade bot ban wave," but individual bans of trade snipers and third-party-software users are real and ongoing, appeals rarely succeed, and GGG does not warn you first. The Terms of Use ([section 7](https://www.pathofexile.com/legal/terms-of-use-and-privacy-policy)) also let GGG close accounts without notice or explanation.

### The realistic risk ranking within this plugin

| Feature | Why it's risky |
|---|---|
| **LiveSearch auto-whisper / auto-teleport** | Highest risk. Automated, game-affecting action driven by a websocket — exactly what GGG named as bannable. |
| **FastMode / BulkBuy automation** | Synthetic input performing multiple game actions per trigger — violates the "one manual action" macro rule. |
| **Everything (the plugin running at all)** | ExileCore reads game memory, which by itself is in GGG's "immediate account termination" category. There is **no** fully safe way to run this. |

By running this plugin you accept these risks yourself. It is provided with no warranty. If your account matters to you, use GGG's in-game trade, or a clipboard-based price checker (e.g. Awakened PoE Trade), which do not read game memory or automate actions.

*A shortened version of this notice is printed to the ExileCore log every time the plugin initializes, so users who don't read this file still see it.*

---

## Features

### LiveSearch
Connect to multiple live trade searches simultaneously with item tracking and (optional, high-risk) auto-teleport.

- Real-time trade search monitoring via WebSocket
- Auto-teleport to seller hideouts *(highest-risk feature — see Account risk above)*
- Smart mouse movement to items in the purchase window
- Sound alerts for new items
- Rate limiting protection
- Secure POESESSID storage (Windows Credential Manager)
- Fast mode for competitive purchases *(automation — high risk)*
- Auto-stash when inventory is full
- Multi-search group management

### LowerPrice
Automatically adjust prices on items in your trade stash.

- Bulk repricing with configurable strategies
- Support for Chaos, Divine, Exalted, and Annulment orbs
- Percentage or flat reduction pricing models
- Currency-specific overrides
- Timer with sound notifications
- Value display showing total worth (requires currency rates — see note below)
- Special handling for 1-currency items

### BulkBuy (In Development)
Purchase multiple items from trade searches automatically. *(Automation — high risk.)*

- Queue-based bulk purchasing
- Configurable delays for human-like behavior
- Emergency stop hotkey
- Multi-search support with per-search limits

---

## Setup

### Session ID
1. Go to pathofexile.com and log in
2. Press F12 to open Developer Tools
3. Go to Application/Storage > Cookies > pathofexile.com
4. Copy the POESESSID value (32 characters)
5. Paste it in the plugin settings

### League (auto-detected)
The plugin now **auto-detects the league your character is in** (via game memory, falling back to GGG's current-league API). You no longer need to set the league manually, and it no longer breaks when a new league launches.

- **Auto-Detect League** (top of settings, ON by default) uses your current league for BulkBuy searches and currency rates.
- Turn it OFF only if you deliberately want to target a manually-set league per search.

> Previous versions hardcoded the league name (`Keepers`), which caused the trade API to reject every request with a generic **"Invalid query"** (HTTP 400) once that league ended. That is fixed.

### LiveSearch
1. Create search groups in settings
2. Add searches by pasting trade URLs from pathofexile.com/trade (the league is read from the URL)
3. Enable the searches you want to track
4. Configure hotkeys and preferences

### LowerPrice
1. Enable LowerPrice in settings
2. Configure pricing strategy (percentage or flat reduction)
3. Select which currencies to reprice
4. Set currency-specific overrides if needed

---

## Notes on external services

### Trade API
GGG's trade endpoints (`/api/trade/search`, `/api/trade/fetch`, `/api/trade/live`, `/api/trade/whisper`) are **not** part of GGG's documented/supported public API. They can change without notice, and using them programmatically is against the Terms of Use (see Account risk). Read-path requests (search / fetch / live search) now send an honest identifying `User-Agent` (`TradeUtils/...`) instead of impersonating Chrome, in line with what GGG's API docs ask for.

### poe.ninja currency rates
LowerPrice fetches currency rates from poe.ninja for the **value display** and cross-currency overrides. Percentage-based repricing does **not** depend on this. As of this writing poe.ninja's old `/api/data/currencyoverview` endpoint appears to have moved, so rate fetching may fail — this is now handled gracefully (the plugin keeps working with default rates and logs a clear message instead of a scary error). Restoring the value display fully may require pointing at poe.ninja's current API in a future update.

---

## Async trading (3.27+) affects results

Path of Exile's asynchronous trade update moved the large majority of listings to **Instant Buyout** (secured/Faustus) trades, which have no whisper and no hideout teleport. If your searches use the `online` / "In Person" status filter, you will see only a small slice of the market. Configure your trade searches to include Instant Buyout (`available` / `securable`) listings for accurate, current results.

---

## Safety Features (technical)

- Built-in rate limiting to reduce API-quota bans
- Burst protection for item processing
- Secure credential storage (Windows Credential Manager)
- Emergency stop hotkeys
- Human-like timing with randomization

*These reduce the chance of tripping GGG's rate limits. They do **not** make automation compliant with the Terms of Use, and they do not remove the ban risk described above.*

---

## Credits

- Ported from TradeUtils (POE2 version)
- Built for ExileCore (POE1)

## License

See LICENSE file for details.
