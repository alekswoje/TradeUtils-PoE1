using System.Numerics;
using System.Runtime.InteropServices;
using System.Drawing; // For POE1 compatibility

namespace TradeUtils.Utility;

internal class Mouse
{
    public enum MouseEvents
    {
        LeftDown = 0x00000002,
        LeftUp = 0x00000004,
        MiddleDown = 0x00000020,
        MiddleUp = 0x00000040,
        Move = 0x00000001,
        Absolute = 0x00008000,
        RightDown = 0x00000008,
        RightUp = 0x00000010
    }

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point lpPoint);

    public static Vector2 GetCursorPosition()
    {
        Point lpPoint;
        GetCursorPos(out lpPoint);
        return new Vector2(lpPoint.X, lpPoint.Y);
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

    public static void moveMouse(Vector2 pos)
    {
        SetCursorPos((int)pos.X, (int)pos.Y);
    }

    public static void LeftDown()
    {
        mouse_event((int)MouseEvents.LeftDown, 0, 0, 0, 0);
    }

    public static void LeftUp()
    {
        mouse_event((int)MouseEvents.LeftUp, 0, 0, 0, 0);
    }

    public static void RightDown()
    {
        mouse_event((int)MouseEvents.RightDown, 0, 0, 0, 0);
    }

    public static void RightUp()
    {
        mouse_event((int)MouseEvents.RightUp, 0, 0, 0, 0);
    }
}

