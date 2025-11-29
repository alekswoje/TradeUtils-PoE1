using System;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;

namespace TradeUtils;

public partial class TradeUtils : BaseSettingsPlugin<TradeUtilsSettings>
{
    // Wrapper properties to allow LiveSearch/LowerPrice/BulkBuy/CurrencyExchange code to access Settings directly
    // This avoids having to change thousands of Settings.X references to Settings.LiveSearch.X
    private LiveSearchSubSettings LiveSearchSettings => Settings.LiveSearch;
    private LowerPriceSubSettings LowerPriceSettings => Settings.LowerPrice;
    private BulkBuySubSettings BulkBuySettings => Settings.BulkBuy;
    private CurrencyExchangeSubSettings CurrencyExchangeSettings => Settings.CurrencyExchange;
    
    public override bool Initialise()
    {
        LogMessage("=== TradeUtils Plugin Initialization Started ===");
        LogMessage($"Plugin Enabled: {Settings.Enable.Value}");
        
        if (!Settings.Enable.Value)
        {
            LogMessage("⚠️  TradeUtils plugin is DISABLED!");
            LogMessage("⚠️  Enable it in: Plugin Settings > Trade Utils > Enable");
            return true; // Return true so plugin doesn't error out
        }
        
        // Set plugin instance reference in settings for GUI access
        Settings.LiveSearch.GroupsConfig.PluginInstance = this;
        Settings.BulkBuy.GroupsConfig.PluginInstance = this;
        
        // Initialize LiveSearch sub-plugin if enabled
        if (Settings.LiveSearch.Enable.Value)
        {
            LogMessage("Initializing LiveSearch...");
            InitializeLiveSearch();
        }
        else
        {
            LogMessage("LiveSearch is DISABLED - skipping initialization");
        }
        
        // Initialize LowerPrice sub-plugin if enabled
        if (Settings.LowerPrice.Enable.Value)
        {
            LogMessage("Initializing LowerPrice...");
            InitializeLowerPrice();
        }
        else
        {
            LogMessage("LowerPrice is DISABLED - skipping initialization");
            LogMessage("⚠️  To enable LowerPrice: Plugin Settings > Trade Utils > Lower Price > Enable");
        }
        
        // Initialize BulkBuy sub-plugin if enabled
        if (Settings.BulkBuy.Enable.Value)
        {
            LogMessage("Initializing BulkBuy...");
            InitializeBulkBuy();
            
            // Apply timing preset on initialization
            string presetStr = Settings.BulkBuy.TimingPreset?.Value ?? "Fast";
            string[] presetNames = { "Slow", "Fast", "SuperFast" };
            int presetIndex = Array.IndexOf(presetNames, presetStr);
            if (presetIndex < 0) presetIndex = 1; // Default to Fast if invalid
            ApplyTimingPreset(presetIndex);
        }
        else
        {
            LogMessage("BulkBuy is DISABLED - skipping initialization");
        }
        
        // Initialize Currency Exchange sub-plugin if enabled
        if (Settings.CurrencyExchange.Enable.Value)
        {
            LogMessage("Initializing Currency Exchange...");
            InitializeCurrencyExchange();
        }
        else
        {
            LogMessage("Currency Exchange is DISABLED - skipping initialization");
        }
        
        LogMessage("=== TradeUtils initialized successfully ===");
        LogMessage($"Status - LiveSearch: {(Settings.LiveSearch.Enable.Value ? "ENABLED ✓" : "DISABLED ✗")}");
        LogMessage($"Status - LowerPrice: {(Settings.LowerPrice.Enable.Value ? "ENABLED ✓" : "DISABLED ✗")}");
        LogMessage($"Status - BulkBuy: {(Settings.BulkBuy.Enable.Value ? "ENABLED ✓" : "DISABLED ✗")}");
        LogMessage($"Status - Currency Exchange: {(Settings.CurrencyExchange.Enable.Value ? "ENABLED ✓" : "DISABLED ✗")}");
        
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        // Propagate area change to LiveSearch if enabled
        if (Settings.LiveSearch.Enable.Value)
        {
            AreaChangeLiveSearch(area);
        }
        
        // Propagate area change to LowerPrice if needed
        if (Settings.LowerPrice.Enable.Value)
        {
            AreaChangeLowerPrice(area);
        }
        
        // Propagate area change to BulkBuy if needed
        if (Settings.BulkBuy.Enable.Value)
        {
            AreaChangeBulkBuy(area);
        }
        
        // Propagate area change to Currency Exchange if needed
        if (Settings.CurrencyExchange.Enable.Value)
        {
            AreaChangeCurrencyExchange(area);
        }
    }

    public override void Dispose()
    {
        DisposeLiveSearch();
        DisposeLowerPrice();
        DisposeBulkBuy();
        DisposeCurrencyExchange();
        base.Dispose();
    }

    public override Job Tick()
    {
        if (!Settings.Enable) return null;

        // Tick LiveSearch if enabled
        if (Settings.LiveSearch.Enable.Value)
        {
            TickLiveSearch();
        }

        // Tick LowerPrice if enabled
        if (Settings.LowerPrice.Enable.Value)
        {
            TickLowerPrice();
        }

        // Tick BulkBuy if enabled
        if (Settings.BulkBuy.Enable.Value)
        {
            TickBulkBuy();
        }
        
        // Tick Currency Exchange if enabled
        if (Settings.CurrencyExchange.Enable.Value)
        {
            TickCurrencyExchange();
        }

        return null;
    }
    
    // Placeholder methods - will be implemented in partial classes
    partial void InitializeLiveSearch();
    partial void InitializeLowerPrice();
    partial void InitializeBulkBuy();
    partial void InitializeCurrencyExchange();
    partial void RenderLowerPrice();
    partial void RenderBulkBuy();
    partial void RenderCurrencyExchange();
    partial void AreaChangeLiveSearch(AreaInstance area);
    partial void AreaChangeLowerPrice(AreaInstance area);
    partial void AreaChangeBulkBuy(AreaInstance area);
    partial void AreaChangeCurrencyExchange(AreaInstance area);
    partial void DisposeLiveSearch();
    partial void DisposeLowerPrice();
    partial void DisposeBulkBuy();
    partial void DisposeCurrencyExchange();
    partial void TickLiveSearch();
    partial void TickLowerPrice();
    partial void TickBulkBuy();
    partial void TickCurrencyExchange();
    
    // Public method for opening all enabled searches in browser (called from settings UI)
    public void OpenAllEnabledSearchesInBrowser()
    {
        if (Settings.LiveSearch.Enable.Value)
        {
            OpenAllEnabledSearchesInBrowserInternal();
        }
        else
        {
            LogMessage("LiveSearch is disabled - enable it first to open searches in browser");
        }
    }
    
    partial void OpenAllEnabledSearchesInBrowserInternal();
}

