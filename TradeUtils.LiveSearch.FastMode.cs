using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;

namespace TradeUtils;

public partial class TradeUtils
{
    /// <summary>
    /// Cache purchase window position when available
    /// </summary>
    private void CachePurchaseWindowPosition()
    {
        try
        {
            var purchaseWindow = GameController?.IngameState?.IngameUi?.PurchaseWindowHideout;
            if (purchaseWindow != null && purchaseWindow.IsVisible)
            {
                var stashContainer = purchaseWindow.TabContainer?.VisibleStash;
                if (stashContainer != null)
                {
                    var stashRect = stashContainer.GetClientRectCache;
                    var topLeft = stashRect.TopLeft;
                    _cachedPurchaseWindowTopLeft = (topLeft.X, topLeft.Y);
                    _hasCachedPosition = true;
                    LogDebug($"📍 CACHED POSITION: Purchase window at ({topLeft.X}, {topLeft.Y})");
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"❌ Error caching purchase window position: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Execute fast mode cursor positioning
    /// </summary>
    private async Task<bool> TryExecuteFastMode()
    {
        try
        {
            var purchaseWindow = GameController?.IngameState?.IngameUi?.PurchaseWindowHideout;
            LogMessage($"🚀 FAST MODE: PurchaseWindow={purchaseWindow != null}");
            
            if (purchaseWindow != null)
            {
                // Get the stash container position - this is already screen coordinates!
                var stashContainer = purchaseWindow.TabContainer?.VisibleStash;
                if (stashContainer != null)
                {
                    var stashRect = stashContainer.GetClientRectCache;
                    var topLeft = stashRect.TopLeft;
                    LogMessage($"🚀 FAST MODE: Stash container rect=({stashRect.X}, {stashRect.Y}, {stashRect.Width}, {stashRect.Height})");
                    LogMessage($"🚀 FAST MODE: Stash container TopLeft=({topLeft.X}, {topLeft.Y})");
                    
                    // Cache this position for future use
                    _cachedPurchaseWindowTopLeft = (topLeft.X, topLeft.Y);
                    _hasCachedPosition = true;
                    
                    // Calculate cell size based on stash container dimensions (assuming 12x12 grid)
                    float cellWidth = stashRect.Width / 12.0f;
                    float cellHeight = stashRect.Height / 12.0f;
                    
                    // Calculate item position within the stash container using TopLeft as base
                    int itemX = (int)(topLeft.X + (_fastModeCoords.x * cellWidth) + (cellWidth * 7 / 8));
                    int itemY = (int)(topLeft.Y + (_fastModeCoords.y * cellHeight) + (cellHeight * 7 / 8));
                    
                    // TopLeft is already in screen coordinates, so no need to add game window offset
                    int finalX = itemX;
                    int finalY = itemY;
                    
                    LogMessage($"🚀 FAST MODE: Calculated position - Item=({itemX}, {itemY}), TopLeft=({topLeft.X}, {topLeft.Y}), Final=({finalX}, {finalY})");
                    
                    // Move mouse cursor
                    System.Windows.Forms.Cursor.Position = new System.Drawing.Point(finalX, finalY);
                    LogMessage($"🚀 FAST MODE: Moved cursor to ({finalX}, {finalY})");
                    
                    // First click will be handled by the main fast mode logic
                    LogMessage("🚀 FAST MODE: Cursor positioned, ready for clicking");
                    return true;
                }
                else
                {
                    LogMessage("🚀 FAST MODE: Stash container is null - waiting for next frame");
                    return false;
                }
            }
            else if (_hasCachedPosition)
            {
                // Use cached position if purchase window is not available
                LogMessage($"🚀 FAST MODE: Using cached position ({_cachedPurchaseWindowTopLeft.x}, {_cachedPurchaseWindowTopLeft.y})");
                
                // Use default cell size (32x32) when we don't have the window
                const float cellWidth = 32.0f;
                const float cellHeight = 32.0f;
                
                int itemX = (int)(_cachedPurchaseWindowTopLeft.x + (_fastModeCoords.x * cellWidth) + (cellWidth * 7 / 8));
                int itemY = (int)(_cachedPurchaseWindowTopLeft.y + (_fastModeCoords.y * cellHeight) + (cellHeight * 7 / 8));
                
                LogMessage($"🚀 FAST MODE: Cached calculation - Item=({itemX}, {itemY}), Cached=({_cachedPurchaseWindowTopLeft.x}, {_cachedPurchaseWindowTopLeft.y}), Final=({itemX}, {itemY})");
                
                // Move mouse cursor
                System.Windows.Forms.Cursor.Position = new System.Drawing.Point(itemX, itemY);
                LogMessage($"🚀 FAST MODE: Moved cursor to ({itemX}, {itemY})");
                
                // First click will be handled by the main fast mode logic
                LogMessage("🚀 FAST MODE: Cursor positioned, ready for clicking");
                return true;
            }
            else
            {
                LogMessage("🚀 FAST MODE: PurchaseWindow is null and no cached position - waiting for next frame");
                return false;
            }
        }
        catch (Exception ex)
        {
            LogError($"🚀 FAST MODE ERROR: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Trigger fast mode for given coordinates
    /// </summary>
    public void TriggerFastMode(int x, int y, string searchId = null)
    {
        bool fastModeEnabled = false;
        
        if (!string.IsNullOrEmpty(searchId))
        {
            var searchConfig = GetSearchConfigBySearchId(searchId);
            if (searchConfig != null)
            {
                fastModeEnabled = searchConfig.FastMode.Value;
            }
        }
        else
        {
            fastModeEnabled = Settings.LiveSearch.FastMode.FastMode.Value;
        }
        
        if (!fastModeEnabled)
        {
            LogDebug("Fast Mode is disabled for this search");
            return;
        }
        
        LogMessage($"🚀 FAST MODE TRIGGERED: Starting for coordinates ({x}, {y})");
        _fastModePending = true;
        _fastModeCoords = (x, y);
        _fastModeStartTime = DateTime.Now;
        _fastModeClickCount = 0;
        _fastModeCtrlPressed = false;
        _fastModeInInitialPhase = true;
        _fastModeRetryCount = 0;
    }
}

