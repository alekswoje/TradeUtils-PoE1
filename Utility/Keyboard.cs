using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TradeUtils.Utility;

internal class Keyboard
{
    private const int KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const int KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    private static extern uint keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

    public static void KeyDown(Keys key)
    {
        keybd_event((byte)key, 0, KEYEVENTF_EXTENDEDKEY | 0, 0);
    }

    public static void KeyUp(Keys key)
    {
        keybd_event((byte)key, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0);
    }

    public static void Type(string text)
    {
        foreach (char c in text)
        {
            Keys key = (Keys)char.ToUpper(c);
            if (char.IsLetterOrDigit(c))
            {
                KeyDown(key);
                KeyUp(key);
            }
        }
    }

    public static void KeyPress(Keys key)
    {
        KeyDown(key);
        KeyUp(key);
    }
}

