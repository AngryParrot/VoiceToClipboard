using System.Runtime.InteropServices;

namespace VoiceToClipboard.Services;

public class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 9000;
    private const int CANCEL_HOTKEY_ID = 9001;
    private const uint VK_ESCAPE = 0x1B;
    private const uint WM_USER = 0x0400;
    private const uint WM_REGISTER_CANCEL = WM_USER + 1;
    private const uint WM_UNREGISTER_CANCEL = WM_USER + 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [Flags]
    private enum Modifiers : uint
    {
        Alt = 0x0001,
        Ctrl = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;

    public event Action? HotkeyPressed;
    public event Action? CancelPressed;

    private IntPtr _hwnd;
    private Thread? _messageThread;
    private volatile bool _disposed;
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate? _wndProcDelegate; // prevent GC

    public void RegisterCancelHotkey()
    {
        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WM_REGISTER_CANCEL, IntPtr.Zero, IntPtr.Zero);
    }

    public void UnregisterCancelHotkey()
    {
        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WM_UNREGISTER_CANCEL, IntPtr.Zero, IntPtr.Zero);
    }

    public bool Register(string hotkeyString)
    {
        ParseHotkey(hotkeyString, out uint modifiers, out uint vk);
        modifiers |= (uint)Modifiers.NoRepeat;

        _wndProcDelegate = WndProc;
        _messageThread = new Thread(() => MessageLoop(modifiers, vk))
        {
            IsBackground = true,
            Name = "HotkeyMessageLoop"
        };
        _messageThread.Start();
        return true;
    }

    private void MessageLoop(uint modifiers, uint vk)
    {
        var hInstance = GetModuleHandle(null);
        var className = "VoiceToClipboardHotkeyWindow";

        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate!),
            hInstance = hInstance,
            lpszClassName = className
        };
        RegisterClass(ref wc);

        _hwnd = CreateWindowEx(0, className, "VoiceToClipboard Hotkey", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero || !RegisterHotKey(_hwnd, HOTKEY_ID, modifiers, vk))
        {
            return;
        }

        while (!_disposed)
        {
            var result = GetMessage(out MSG msg, IntPtr.Zero, 0, 0);
            if (result <= 0) break;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnregisterHotKey(_hwnd, CANCEL_HOTKEY_ID);
        UnregisterHotKey(_hwnd, HOTKEY_ID);
        DestroyWindow(_hwnd);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (id == HOTKEY_ID)
            {
                HotkeyPressed?.Invoke();
                return IntPtr.Zero;
            }
            if (id == CANCEL_HOTKEY_ID)
            {
                CancelPressed?.Invoke();
                return IntPtr.Zero;
            }
        }
        else if (msg == WM_REGISTER_CANCEL)
        {
            RegisterHotKey(hWnd, CANCEL_HOTKEY_ID, (uint)Modifiers.NoRepeat, VK_ESCAPE);
            return IntPtr.Zero;
        }
        else if (msg == WM_UNREGISTER_CANCEL)
        {
            UnregisterHotKey(hWnd, CANCEL_HOTKEY_ID);
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void ParseHotkey(string hotkey, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        var parts = hotkey.Split('+', StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= (uint)Modifiers.Ctrl;
                    break;
                case "alt":
                    modifiers |= (uint)Modifiers.Alt;
                    break;
                case "shift":
                    modifiers |= (uint)Modifiers.Shift;
                    break;
                case "win":
                    modifiers |= (uint)Modifiers.Win;
                    break;
                default:
                    vk = part.Length == 1
                        ? (uint)char.ToUpperInvariant(part[0])
                        : part.ToUpperInvariant() switch
                        {
                            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73,
                            "F5" => 0x74, "F6" => 0x75, "F7" => 0x76, "F8" => 0x77,
                            "F9" => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
                            "SPACE" => 0x20, "ENTER" => 0x0D, "ESCAPE" => 0x1B,
                            _ => (uint)char.ToUpperInvariant(part[0])
                        };
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        _messageThread?.Join(2000);
    }
}
