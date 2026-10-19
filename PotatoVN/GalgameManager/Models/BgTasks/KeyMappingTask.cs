using System.Diagnostics;
using System.Runtime.InteropServices;
using GalgameManager.Contracts.Services;
using GalgameManager.Enums;
using GalgameManager.Helpers;
using GalgameManager.Models;
using Windows.System;
using WindowsInput;
using WindowsInput.Native;

namespace GalgameManager.Models.BgTasks;

public class KeyMappingTask : BgTaskBase
{
    public string ProcessName { get; set; } = string.Empty;
    public Galgame? Galgame;
    public override bool ProgressOnTrayIcon => false;
    public List<KeyMapping> KeyMappings { get; set; } = new();

    private Process? _process;
    private nint _keyboardHookId = nint.Zero;
    private nint _mouseHookId = nint.Zero;
    private readonly IInputSimulator _inputSimulator = new InputSimulator();
    private readonly Dictionary<string, (List<VirtualKeyCode> modifiers, VirtualKeyCode? key, int? mouseButton)> _lookupMap = new();

    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);
    private delegate nint LowLevelMouseProc(int nCode, nint wParam, nint lParam);
    private LowLevelKeyboardProc? _keyboardHookProc;
    private LowLevelMouseProc? _mouseHookProc;

    #region P_INVOKE
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

    // 鼠标事件常量
    private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
    private const uint MOUSEEVENTF_LEFTUP = 0x04;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x08;
    private const uint MOUSEEVENTF_RIGHTUP = 0x10;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x20;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x40;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    #endregion
    
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    // 鼠标常量
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_MOUSEWHEEL = 0x020A;

    public KeyMappingTask() { }

    public KeyMappingTask(Galgame game, Process process)
    {
        ProcessName = process.ProcessName;
        Galgame = game;
        _process = process;
        KeyMappings = game.KeyMappings.Select(m => new KeyMapping
        {
            From = new List<int>(m.From),
            To = new List<int>(m.To),
            IsEnabled = m.IsEnabled,
            Remark = m.Remark
        }).ToList();
    }

    protected override Task RecoverFromJsonInternal()
    {
        _process = Process.GetProcessesByName(ProcessName).FirstOrDefault();
        return Task.CompletedTask;
    }

    protected async override Task RunInternal()
    {
        if (_process is null || Galgame is null) return;

        BuildLookupMap();
        if (_lookupMap.Count == 0) return;

        ChangeProgress(0, 1, "KeyMappingTask_ProgressMsg".GetLocalized(Galgame.Name.Value!));

        _keyboardHookProc = KeyboardHookCallback;
        _mouseHookProc = MouseHookCallback;
        _keyboardHookId = SetKeyboardHook(_keyboardHookProc);
        _mouseHookId = SetMouseHook(_mouseHookProc);

        try
        {
            await _process.WaitForExitAsync();
        }
        finally
        {
            if (_keyboardHookId != nint.Zero)
            {
                UnhookWindowsHookEx(_keyboardHookId);
                _keyboardHookId = nint.Zero;
            }
            if (_mouseHookId != nint.Zero)
            {
                UnhookWindowsHookEx(_mouseHookId);
                _mouseHookId = nint.Zero;
            }
            _keyboardHookProc = null;
            _mouseHookProc = null;
            ChangeProgress(1, 1, "KeyMappingTask_Done".GetLocalized());
        }
    }

    private void BuildLookupMap()
    {
        foreach (var mapping in KeyMappings)
        {
            if (!mapping.IsEnabled || mapping.From.Count == 0 || mapping.To.Count == 0) continue;

            // 检查是否是鼠标到键盘的映射
            var fromMouseButton = mapping.From.FirstOrDefault(IsMouseButtonCode);
            if (fromMouseButton != 0)
            {
                // 鼠标映射到键盘
                var toKeys = mapping.To.Select(k => (VirtualKey)k).ToList();
                var toModifiers = toKeys.Where(IsModifierKey).Select(k => (VirtualKeyCode)k).ToList();
                var toKey = toKeys.FirstOrDefault(k => !IsModifierKey(k));
                if (toKey == default)
                {
                    toKey = toKeys.FirstOrDefault();
                }

                var fromKeyString = $"Mouse{fromMouseButton}";
                _lookupMap[fromKeyString] = (toModifiers, (VirtualKeyCode)toKey, null);
            }
            else
            {
                // 键盘映射（到键盘或鼠标）
                var fromKeys = mapping.From.Select(k => (VirtualKey)k).ToList();
                fromKeys.Sort((a, b) => GetKeyOrder(a) - GetKeyOrder(b));
                var fromKeyString = string.Join("+", fromKeys.Select(GetKeyDisplayName));

                // 检查是否包含鼠标按键
                var mouseButton = mapping.To.FirstOrDefault(IsMouseButtonCode);
                if (mouseButton != 0)
                {
                    // 键盘映射到鼠标
                    _lookupMap[fromKeyString] = (new List<VirtualKeyCode>(), null, mouseButton);
                }
                else
                {
                    // 键盘映射到键盘
                    var toKeys = mapping.To.Select(k => (VirtualKey)k).ToList();
                    var toModifiers = toKeys.Where(IsModifierKey).Select(k => (VirtualKeyCode)k).ToList();
                    var toKey = toKeys.FirstOrDefault(k => !IsModifierKey(k));
                    if (toKey == default)
                    {
                        toKey = toKeys.FirstOrDefault();
                    }
                    _lookupMap[fromKeyString] = (toModifiers, (VirtualKeyCode)toKey, null);
                }
            }
        }
    }

    private static bool IsMouseButtonCode(int code) => code switch
    {
        1 => true, // 鼠标左键
        2 => true, // 鼠标右键
        3 => true, // 鼠标中键
        4 => true, // X1按钮
        5 => true, // X2按钮
        6 => true, // 鼠标滚轮向上
        7 => true, // 鼠标滚轮向下
        _ => false
    };

    private nint SetKeyboardHook(LowLevelKeyboardProc proc)
    {
        using Process curProcess = Process.GetCurrentProcess();
        using ProcessModule curModule = curProcess.MainModule!;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName!), 0);
    }

    private nint SetMouseHook(LowLevelMouseProc proc)
    {
        using Process curProcess = Process.GetCurrentProcess();
        using ProcessModule curModule = curProcess.MainModule!;
        return SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(curModule.ModuleName!), 0);
    }

    private nint KeyboardHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

        try
        {
            if (_process is null || _process.HasExited) return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

            var foregroundWindowHandle = GetForegroundWindow();
            GetWindowThreadProcessId(foregroundWindowHandle, out var foregroundProcessId);
            if (_process.Id != foregroundProcessId) return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

            if (wParam != WM_KEYDOWN && wParam != WM_SYSKEYDOWN) return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

            var vkCode = (VirtualKey)Marshal.ReadInt32(lParam);
            if (IsModifierKey(vkCode)) return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);

            List<VirtualKey> pressedKeys = new() { vkCode };
            if (IsKeyDown(VirtualKey.Control)) pressedKeys.Add(VirtualKey.Control);
            if (IsKeyDown(VirtualKey.Shift)) pressedKeys.Add(VirtualKey.Shift);
            if (IsKeyDown(VirtualKey.Menu)) pressedKeys.Add(VirtualKey.Menu); // Alt
            if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) pressedKeys.Add(VirtualKey.LeftWindows);

            pressedKeys.Sort((a, b) => GetKeyOrder(a) - GetKeyOrder(b));
            var keyString = string.Join("+", pressedKeys.Select(GetKeyDisplayName));

            if (_lookupMap.TryGetValue(keyString, out var toHotkey))
            {
                if (toHotkey.mouseButton.HasValue)
                {
                    // 映射到鼠标按键
                    SimulateMouseClick(toHotkey.mouseButton.Value);
                }
                else if (toHotkey.key.HasValue)
                {
                    // 映射到键盘按键
                    _inputSimulator.Keyboard.ModifiedKeyStroke(toHotkey.modifiers, toHotkey.key.Value);
                }
                return 1;
            }
        }
        catch(Exception)
        {
            // Ignored
        }
        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private nint MouseHookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);

        try
        {
            if (_process is null || _process.HasExited) return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);

            var foregroundWindowHandle = GetForegroundWindow();
            GetWindowThreadProcessId(foregroundWindowHandle, out var foregroundProcessId);
            if (_process.Id != foregroundProcessId) return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);

            // 检查是否是需要处理的鼠标事件
            if (!IsMouseEvent(wParam)) return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);

            var mouseButton = GetMouseButtonFromWParam(wParam, lParam);
            if (mouseButton == 0) return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);

            // 处理鼠标到键盘的映射
            var fromKeyString = $"Mouse{mouseButton}";
            if (_lookupMap.TryGetValue(fromKeyString, out var toHotkey))
            {
                if (toHotkey.key.HasValue)
                {
                    // 鼠标映射到键盘按键
                    _inputSimulator.Keyboard.ModifiedKeyStroke(toHotkey.modifiers, toHotkey.key.Value);
                }
                return 1; // 阻止原始鼠标事件
            }
        }
        catch(Exception)
        {
            // Ignored
        }
        return CallNextHookEx(_mouseHookId, nCode, wParam, lParam);
    }

    private void SimulateMouseClick(int mouseButton)
    {
        switch (mouseButton)
        {
            case 1: // 左键
                mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                break;
            case 2: // 右键
                mouse_event(MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                break;
            case 3: // 中键
                mouse_event(MOUSEEVENTF_MIDDLEDOWN | MOUSEEVENTF_MIDDLEUP, 0, 0, 0, 0);
                break;
            case 4: // X1键
            case 5: // X2键
                mouse_event(MOUSEEVENTF_XDOWN, 0, 0, (uint)(mouseButton == 4 ? 1 : 2), 0);
                mouse_event(MOUSEEVENTF_XUP, 0, 0, (uint)(mouseButton == 4 ? 1 : 2), 0);
                break;
            case 6: // 鼠标滚轮向上
                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, 120, 0);
                break;
            case 7: // 鼠标滚轮向下
                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)-120), 0);
                break;
        }
    }

    private static bool IsMouseEvent(nint wParam) => wParam switch
    {
        WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN or WM_MOUSEWHEEL => true,
        _ => false
    };

    private static int GetMouseButtonFromWParam(nint wParam, nint lParam) => wParam switch
    {
        WM_LBUTTONDOWN => 1,
        WM_RBUTTONDOWN => 2,
        WM_MBUTTONDOWN => 3,
        WM_XBUTTONDOWN => GetXButtonFromLParam(lParam), // 读取额外数据确定X1/X2
        WM_MOUSEWHEEL => GetWheelDirectionFromLParam(lParam), // 确定滚轮方向
        _ => 0
    };

    private static int GetXButtonFromLParam(nint lParam)
    {
        // 高位字表示X按钮编号
        var hiWord = (ushort)((uint)Marshal.ReadInt32(lParam) >> 16);
        return hiWord == 1 ? 4 : 5; // X1=4, X2=5
    }

    private static int GetWheelDirectionFromLParam(nint lParam)
    {
        // 高位字表示滚轮方向
        var hiWord = (short)((uint)Marshal.ReadInt32(lParam) >> 16);
        return hiWord > 0 ? 6 : 7; // 向上=6, 向下=7
    }
    
    private bool IsKeyDown(VirtualKey key) => (GetKeyState((int)key) & 0x8000) != 0;

    private static bool IsModifierKey(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.Menu or VirtualKey.LeftMenu 
        or VirtualKey.RightMenu or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static int GetKeyOrder(VirtualKey key)
    {
        if (key is VirtualKey.LeftWindows or VirtualKey.RightWindows) return 1;
        if (key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl) return 2;
        if (key is VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu) return 3;
        if (key is VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift) return 4;
        return 5;
    }
    
    private static string GetKeyDisplayName(VirtualKey key) => key switch
    {
        VirtualKey.LeftWindows or VirtualKey.RightWindows => "Win",
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => "Ctrl",
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => "Alt",
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => "Shift",
        _ => key.ToString()
    };

    public override string Title { get; } = "KeyMappingTask_Title".GetLocalized();
}