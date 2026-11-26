using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace LiteVLC.Controls;

/// <summary>
/// Ultra-lightweight native Win32 child HWND host for Direct3D11 libmpv rendering.
/// Filters synthetic WM_MOUSEMOVE events caused by HWND resizes and supports clean cursor auto-hide.
/// </summary>
public sealed class VideoHost : HwndHost
{
    private IntPtr _hwnd;
    private string? _windowClassName;
    private IntPtr _arrowCursor;
    private IntPtr _bgBrush;
    private bool _cursorHidden;
    private bool _hasLastCursor;
    private int _lastCursorX;
    private int _lastCursorY;
    private int _lastClickMsgTime;
    private bool _hasLastClick;
    private int _lastClickX;
    private int _lastClickY;
    private int _lastDblClickMsgTime;
    private bool _hasLastDblClick;

    public IntPtr VideoHandle => _hwnd;

    /// <summary>
    /// True only while libmpv is actively presenting D3D11 video frames.
    /// When false (idle, loading, audio-only), WM_ERASEBKGND paints solid dark #090B10 so the HWND is NEVER transparent!
    /// </summary>
    public bool IsRenderingVideo { get; set; }

    public event Action? VideoDoubleClicked;
    public event Action? VideoSingleClicked;
    public event Action? VideoRightClicked;
    public event Action<int>? MouseWheelDelta;
    public event Action? VideoMouseMoved;
    public event Action<string[]>? FilesDropped;

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const uint CS_DBLCLKS = 0x0008;

    private const int WM_SETCURSOR = 0x0020;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_DROPFILES = 0x0233;
    private const int HTCLIENT = 1;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private WndProcDelegate? _wndProcDelegate;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
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
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetMessageTime();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    /// <summary>
    /// Snapshots current physical cursor coordinates so subsequent synthetic WM_MOUSEMOVE
    /// caused by layout/HWND resizes are completely ignored.
    /// </summary>
    public void SyncCursorPosition()
    {
        if (GetCursorPos(out POINT pt))
        {
            _lastCursorX = pt.X;
            _lastCursorY = pt.Y;
            _hasLastCursor = true;
        }
    }

    public void SetCursorHidden(bool hidden)
    {
        if (_cursorHidden == hidden) return;
        _cursorHidden = hidden;
        if (_hwnd != IntPtr.Zero)
            SetCursor(hidden ? IntPtr.Zero : _arrowCursor);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        IntPtr hInstance = GetModuleHandleW(null);
        _windowClassName = $"LiteVLC_VideoHost_{Guid.NewGuid():N}";
        _wndProcDelegate = CustomWndProc;
        _arrowCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512); // IDC_ARROW
        _bgBrush = CreateSolidBrush(0x000F0A08); // Solid obsidian #080A0F (COLORREF is 0x00BBGGRR)

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_DBLCLKS,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInstance,
            hCursor = _arrowCursor,
            hbrBackground = _bgBrush,
            lpszClassName = _windowClassName
        };

        RegisterClassExW(ref wc);

        _hwnd = CreateWindowExW(
            0,
            _windowClassName,
            "",
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0,
            Math.Max(1, (int)ActualWidth),
            Math.Max(1, (int)ActualHeight),
            hwndParent.Handle,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            DragAcceptFiles(_hwnd, true);
            SyncCursorPosition();
        }

        return new HandleRef(this, _hwnd);
    }

    public void RepaintSolidBackground()
    {
        if (_hwnd != IntPtr.Zero && !IsRenderingVideo)
            InvalidateRect(_hwnd, IntPtr.Zero, true);
    }

    private IntPtr CustomWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                if (!IsRenderingVideo && _bgBrush != IntPtr.Zero && GetClientRect(hWnd, out RECT rc))
                    FillRect(wParam, ref rc, _bgBrush);
                return (IntPtr)1;

            case WM_SETCURSOR:
                if ((lParam.ToInt64() & 0xFFFF) == HTCLIENT)
                {
                    SetCursor(_cursorHidden ? IntPtr.Zero : _arrowCursor);
                    return (IntPtr)1;
                }
                break;

            case WM_LBUTTONDBLCLK:
            {
                int msgTime = GetMessageTime();
                _hasLastClick = false;
                if (!_hasLastDblClick || unchecked(msgTime - _lastDblClickMsgTime) > 150)
                {
                    _hasLastDblClick = true;
                    _lastDblClickMsgTime = msgTime;
                    SyncCursorPosition();
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, () => VideoDoubleClicked?.Invoke());
                }
                return IntPtr.Zero;
            }

            case WM_LBUTTONDOWN:
            {
                int msgTime = GetMessageTime();
                int x = (short)(lParam.ToInt64() & 0xFFFF);
                int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                int dblWindow = (int)Math.Clamp(GetDoubleClickTime(), 320u, 550u);

                // Use Win32 GetMessageTime() (true click post time, immune to UI thread stalls) and ±16px
                // tolerance so double-click is NEVER missed even with slight high-DPI mouse jitter!
                int delta = unchecked(msgTime - _lastClickMsgTime);
                if (_hasLastClick &&
                    delta >= 0 && delta <= dblWindow &&
                    Math.Abs(x - _lastClickX) <= 16 &&
                    Math.Abs(y - _lastClickY) <= 16)
                {
                    _hasLastClick = false;
                    _hasLastDblClick = true;
                    _lastDblClickMsgTime = msgTime;
                    SyncCursorPosition();
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, () => VideoDoubleClicked?.Invoke());
                }
                else
                {
                    _hasLastClick = true;
                    _lastClickMsgTime = msgTime;
                    _lastClickX = x;
                    _lastClickY = y;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => VideoSingleClicked?.Invoke());
                }
                return IntPtr.Zero;
            }

            case WM_RBUTTONUP:
                Dispatcher.BeginInvoke(() => VideoRightClicked?.Invoke());
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                if (GetCursorPos(out POINT pt))
                {
                    if (!_hasLastCursor)
                    {
                        _lastCursorX = pt.X;
                        _lastCursorY = pt.Y;
                        _hasLastCursor = true;
                        break;
                    }
                    int dx = Math.Abs(pt.X - _lastCursorX);
                    int dy = Math.Abs(pt.Y - _lastCursorY);
                    // Only notify on real physical mouse movement (>= 6px); ignore synthetic resize WM_MOUSEMOVE!
                    if (dx >= 6 || dy >= 6)
                    {
                        _lastCursorX = pt.X;
                        _lastCursorY = pt.Y;
                        Dispatcher.BeginInvoke(() => VideoMouseMoved?.Invoke());
                    }
                }
                break;

            case WM_MOUSEWHEEL:
            {
                short delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                Dispatcher.BeginInvoke(() => MouseWheelDelta?.Invoke(delta));
                return IntPtr.Zero;
            }

            case WM_DROPFILES:
            {
                IntPtr hDrop = wParam;
                try
                {
                    uint count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
                    var files = new List<string>((int)count);
                    for (uint i = 0; i < count; i++)
                    {
                        uint len = DragQueryFileW(hDrop, i, null, 0);
                        var sb = new StringBuilder((int)len + 1);
                        DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity);
                        files.Add(sb.ToString());
                    }
                    if (files.Count > 0)
                        Dispatcher.BeginInvoke(() => FilesDropped?.Invoke(files.ToArray()));
                }
                finally
                {
                    DragFinish(hDrop);
                }
                return IntPtr.Zero;
            }
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
        if (!string.IsNullOrEmpty(_windowClassName))
            UnregisterClassW(_windowClassName, GetModuleHandleW(null));
        if (_bgBrush != IntPtr.Zero)
        {
            DeleteObject(_bgBrush);
            _bgBrush = IntPtr.Zero;
        }
        _hwnd = IntPtr.Zero;
    }
}
