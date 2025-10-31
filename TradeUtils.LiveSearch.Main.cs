using System;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;

namespace TradeUtils;

public partial class TradeUtils : BaseSettingsPlugin<TradeUtilsSettings>
{
    // Wrapper properties to allow LiveSearch/LowerPrice/BulkBuy code to access Settings directly
    // This avoids having to change thousands of Settings.X references to Settings.LiveSearch.X
    private LiveSearchSubSettings LiveSearchSettings => Settings.LiveSearch;
    private LowerPriceSubSettings LowerPriceSettings => Settings.LowerPrice;
    private BulkBuySubSettings BulkBuySettings => Settings.BulkBuy;
    
    public override bool Initialise()
    {
        // Set plugin instance reference in settings for GUI access
        Settings.LiveSearch.GroupsConfig.PluginInstance = this;
        Settings.BulkBuy.GroupsConfig.PluginInstance = this;
        
        // Initialize LiveSearch sub-plugin if enabled
        if (Settings.LiveSearch.General.Enable.Value)
        {
            InitializeLiveSearch();
        }
        
        // Initialize LowerPrice sub-plugin if enabled
        if (Settings.LowerPrice.Enable.Value)
        {
            InitializeLowerPrice();
        }
        
        // Initialize BulkBuy sub-plugin if enabled
        if (Settings.BulkBuy.Enable.Value)
        {
            InitializeBulkBuy();
        }
        
        LogMessage("TradeUtils initialized successfully");
        LogMessage($"LiveSearch: {(Settings.LiveSearch.General.Enable.Value ? "ENABLED" : "DISABLED")}");
        LogMessage($"LowerPrice: {(Settings.LowerPrice.Enable.Value ? "ENABLED" : "DISABLED")}");
        LogMessage($"BulkBuy: {(Settings.BulkBuy.Enable.Value ? "ENABLED" : "DISABLED")}");
        
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        // Propagate area change to LiveSearch if enabled
        if (Settings.LiveSearch.General.Enable.Value)
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
    }

    public override void Dispose()
    {
        DisposeLiveSearch();
        DisposeLowerPrice();
        DisposeBulkBuy();
        base.Dispose();
    }

    public override Job Tick()
    {
        if (!Settings.Enable) return null;

        // Tick LiveSearch if enabled
        if (Settings.LiveSearch.General.Enable.Value)
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

        return null;
    }
    
    // Placeholder methods - will be implemented in partial classes
    partial void InitializeLiveSearch();
    partial void InitializeLowerPrice();
    partial void InitializeBulkBuy();
    partial void RenderLowerPrice();
    partial void RenderBulkBuy();
    partial void AreaChangeLiveSearch(AreaInstance area);
    partial void AreaChangeLowerPrice(AreaInstance area);
    partial void AreaChangeBulkBuy(AreaInstance area);
    partial void DisposeLiveSearch();
    partial void DisposeLowerPrice();
    partial void DisposeBulkBuy();
    partial void TickLiveSearch();
    partial void TickLowerPrice();
    partial void TickBulkBuy();
    
    // Public method for opening all enabled searches in browser (called from settings UI)
    public void OpenAllEnabledSearchesInBrowser()
    {
        if (Settings.LiveSearch.General.Enable.Value)
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

