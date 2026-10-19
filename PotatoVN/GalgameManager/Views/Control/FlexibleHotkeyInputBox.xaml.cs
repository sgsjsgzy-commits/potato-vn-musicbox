using System.ComponentModel;
using System.Runtime.CompilerServices;
using Windows.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using CommunityToolkit.WinUI;

namespace GalgameManager.Views.Control;

public enum InputMode
{
    Capture,
    Dropdown
}

public sealed partial class FlexibleHotkeyInputBox : INotifyPropertyChanged
{
    private readonly HashSet<VirtualKey> _pressedKeys = new();
    private readonly HashSet<int> _mouseButtons = new();
    private bool _isCapturing;
    private int _clickCount = 0;
    private string _buttonText = "";
    private string _hotkeyDisplayText = "";
    private Visibility _showPlaceholder = Visibility.Visible;
    private Visibility _hideText = Visibility.Collapsed;
    private Visibility _captureButtonVisibility = Visibility.Visible;
    private Visibility _dropdownVisibility = Visibility.Collapsed;

    public FlexibleHotkeyInputBox()
    {
        InitializeComponent();
        UpdateButtonText();

        // 延迟初始化模式显示，确保控件完全加载
        Loaded += (sender, e) =>
        {
            // 如果没有通过 XAML 绑定设置 InputMode，尝试自动查找父级的 InputMode
            if (InputMode == InputMode.Capture)
            {
                var parentInputMode = FindParentInputMode(this);
                if (parentInputMode.HasValue)
                {
                    InputMode = parentInputMode.Value;
                }
            }
            UpdateDisplay();
        };
    }

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(nameof(PlaceholderText), typeof(string), typeof(FlexibleHotkeyInputBox),
            new PropertyMetadata(""));

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public static readonly DependencyProperty HotkeyKeysProperty =
        DependencyProperty.Register(nameof(HotkeyKeys), typeof(List<int>), typeof(FlexibleHotkeyInputBox),
            new PropertyMetadata(new List<int>(), OnHotkeyKeysChanged));

    public List<int> HotkeyKeys
    {
        get => (List<int>)GetValue(HotkeyKeysProperty);
        set => SetValue(HotkeyKeysProperty, value);
    }

    public static readonly DependencyProperty InputModeProperty =
        DependencyProperty.Register(nameof(InputMode), typeof(InputMode), typeof(FlexibleHotkeyInputBox),
            new PropertyMetadata(InputMode.Capture, OnInputModeChanged));

    public InputMode InputMode
    {
        get => (InputMode)GetValue(InputModeProperty);
        set => SetValue(InputModeProperty, value);
    }

    private static void OnInputModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FlexibleHotkeyInputBox control)
        {
            control.UpdateModeDisplay();
        }
    }

    private static void OnHotkeyKeysChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FlexibleHotkeyInputBox control)
        {
            // 根据当前模式更新显示
            if (control.InputMode == InputMode.Dropdown)
            {
                // 在下拉模式下，先更新选择，然后更新显示文本
                control.UpdateDropdownSelection();
                control.UpdateDisplay();
            }
            else
            {
                // 在捕获模式下，正常更新显示
                control.UpdateDisplay();
            }
        }
    }

    public string ButtonText
    {
        get => _buttonText;
        private set
        {
            _buttonText = value;
            OnPropertyChanged();
        }
    }

    public string HotkeyDisplayText
    {
        get => _hotkeyDisplayText;
        private set
        {
            _hotkeyDisplayText = value;
            OnPropertyChanged();
        }
    }

    public Visibility ShowPlaceholder
    {
        get => _showPlaceholder;
        private set
        {
            _showPlaceholder = value;
            OnPropertyChanged();
        }
    }

    public Visibility HideText
    {
        get => _hideText;
        private set
        {
            _hideText = value;
            OnPropertyChanged();
        }
    }

    public Visibility CaptureButtonVisibility
    {
        get => _captureButtonVisibility;
        private set
        {
            _captureButtonVisibility = value;
            OnPropertyChanged();
        }
    }

    public Visibility DropdownVisibility
    {
        get => _dropdownVisibility;
        private set
        {
            _dropdownVisibility = value;
            OnPropertyChanged();
        }
    }


    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    
    private void InputButton_Click(object sender, RoutedEventArgs e)
    {
        _clickCount++;

        if (!_isCapturing)
        {
            // 奇数次点击：开始捕获
            if (_clickCount % 2 == 1)
            {
                StartCapturing();
            }
        }
        else
        {
            // 偶数次点击：捕获鼠标左键
            if (_clickCount % 2 == 0)
            {
                _mouseButtons.Add(1);
                CompleteCapture();
            }
        }
    }

    private void InputButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isCapturing) return;

        e.Handled = true;

        // 添加任何按键
        _pressedKeys.Add(e.Key);

        // 任何按键都完成捕获，不需要修饰键
        CompleteCapture();

        UpdateCaptureDisplay();
    }

    private void InputButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 在捕获状态下处理其他鼠标按键（除了左键）
        if (!_isCapturing) return;

        var pointerPoint = e.GetCurrentPoint(InputButton);
        var properties = pointerPoint.Properties;

        // 检测其他鼠标按键（不包括左键，左键通过点击计数处理）
        if (properties.IsRightButtonPressed)
        {
            _mouseButtons.Add(2);
            CompleteCapture();
        }
        else if (properties.IsMiddleButtonPressed)
        {
            _mouseButtons.Add(3);
            CompleteCapture();
        }
        else if (properties.IsXButton1Pressed)
        {
            _mouseButtons.Add(4);
            CompleteCapture();
        }
        else if (properties.IsXButton2Pressed)
        {
            _mouseButtons.Add(5);
            CompleteCapture();
        }
    }

    private void InputButton_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_isCapturing) return;

        var pointerPoint = e.GetCurrentPoint(InputButton);
        var properties = pointerPoint.Properties;

        if (properties.MouseWheelDelta != 0)
        {
            // 区分滚轮方向：向上为6，向下为7
            var wheelCode = properties.MouseWheelDelta > 0 ? 6 : 7;
            _mouseButtons.Add(wheelCode);
            CompleteCapture();
        }
    }

    private void InputButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isCapturing)
        {
            CancelCapture();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        HotkeyKeys = new List<int>();
        OnHotkeyChanged();
    }

    private void StartCapturing()
    {
        _isCapturing = true;
        _pressedKeys.Clear();
        _mouseButtons.Clear();
        ButtonText = "KeyMappingDialog_Placeholder_PressKey".GetLocalized() ?? "请按下快捷键或鼠标按键";
        ShowPlaceholder = Visibility.Collapsed;
        HideText = Visibility.Collapsed;
        InputButton.Focus(FocusState.Programmatic);
    }

    private void CompleteCapture()
    {
        _isCapturing = false;

        List<int> keys = new();

        // 添加键盘按键
        keys.AddRange(_pressedKeys.OrderBy(GetKeyOrder).Select(k => (int)k));

        // 添加鼠标按键（如果有）
        if (_mouseButtons.Count > 0)
        {
            // 如果捕获到鼠标按键，只保留第一个鼠标按键
            keys.Add(_mouseButtons.First());
        }

        HotkeyKeys = keys;
        _clickCount = 0; // 重置点击计数

        UpdateDisplay();
        OnHotkeyChanged();
    }

    private void CancelCapture()
    {
        _isCapturing = false;
        _clickCount = 0; // 重置点击计数
        UpdateDisplay();
    }

    private void UpdateCaptureDisplay()
    {
        if (_isCapturing)
        {
            List<string> displayParts = new();

            if (_pressedKeys.Count > 0)
            {
                IOrderedEnumerable<VirtualKey> sortedKeys = _pressedKeys.OrderBy(GetKeyOrder);
                displayParts.AddRange(sortedKeys.Select(GetKeyDisplayName));
            }

            if (_mouseButtons.Count > 0)
            {
                displayParts.Add(GetMouseDisplayName(_mouseButtons.First()));
            }

            if (displayParts.Count > 0)
            {
                ButtonText = string.Join(" + ", displayParts);
            }
        }
    }

    private void UpdateDisplay()
    {
        if (HotkeyKeys.Count == 0)
        {
            ButtonText = "";
            HotkeyDisplayText = "";
            ShowPlaceholder = Visibility.Visible;
            HideText = Visibility.Collapsed;
        }
        else
        {
            var displayParts = new List<string>();

            foreach (var keyCode in HotkeyKeys)
            {
                if (IsMouseKeyCode(keyCode))
                {
                    displayParts.Add(GetMouseDisplayName(keyCode));
                }
                else
                {
                    displayParts.Add(GetKeyDisplayName((VirtualKey)keyCode));
                }
            }

            // 排序：键盘按键在前，鼠标按键在后
            var sortedParts = displayParts
                .Select((part, index) => new { Part = part, Index = index, IsMouse = IsMouseKeyCode(HotkeyKeys[index]) })
                .OrderBy(x => x.IsMouse ? 1 : 0)
                .ThenBy(x => x.Index)
                .Select(x => x.Part);

            var displayText = string.Join(" + ", sortedParts);

            if (_isCapturing)
            {
                ButtonText = displayText;
                HotkeyDisplayText = "";
                ShowPlaceholder = Visibility.Collapsed;
                HideText = Visibility.Collapsed;
            }
            else if (InputMode == InputMode.Capture)
            {
                // 在捕获模式且非捕获状态下，显示在HotkeyDisplayText上
                ButtonText = "";
                HotkeyDisplayText = displayText;
                ShowPlaceholder = Visibility.Collapsed;
                HideText = Visibility.Visible;
            }
            else
            {
                // 在下拉模式下，只显示背景文本，避免重影
                ButtonText = "";
                HotkeyDisplayText = displayText;
                ShowPlaceholder = Visibility.Collapsed;
                HideText = Visibility.Visible;
            }
        }

        UpdateButtonText();
    }

    private void UpdateButtonText()
    {
        if (!_isCapturing && (HotkeyKeys.Count == 0))
        {
            ButtonText = "KeyMappingDialog_Placeholder_ClickToSet".GetLocalized() ?? "点击设置";
        }
    }

    private void UpdateModeDisplay()
    {
        try
        {
            if (InputMode == InputMode.Dropdown)
            {
                CaptureButtonVisibility = Visibility.Collapsed;
                DropdownVisibility = Visibility.Visible;

                // 确保下拉列表已正确初始化
                EnsureDropdownInitialized();

                // 更新下拉选择，确保在有按键时正确显示
                UpdateDropdownSelection();

                // 根据是否有按键来设置显示文本
                if (HotkeyKeys.Count > 0)
                {
                    ShowPlaceholder = Visibility.Collapsed;
                    HideText = Visibility.Visible;
                    ButtonText = "";

                    // 更新显示，确保背景文本和下拉框文本都正确
                    UpdateDisplay();
                }
                else
                {
                    ShowPlaceholder = Visibility.Visible;
                    HideText = Visibility.Collapsed;
                    ButtonText = "";
                }
            }
            else
            {
                CaptureButtonVisibility = Visibility.Visible;
                DropdownVisibility = Visibility.Collapsed;

                // 更新显示，确保在有按键时正确显示
                UpdateDisplay();
            }
        }
        catch (Exception ex)
        {
            // 记录异常但不中断程序
            System.Diagnostics.Debug.WriteLine($"UpdateModeDisplay error: {ex.Message}");
            // 如果出错，默认回到捕获模式
            CaptureButtonVisibility = Visibility.Visible;
            DropdownVisibility = Visibility.Collapsed;
        }
    }

    private static bool IsModifierKey(VirtualKey key)
    {
        return key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
                    or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
                    or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
                    or VirtualKey.LeftWindows or VirtualKey.RightWindows;
    }

    private static int GetKeyOrder(VirtualKey key)
    {
        return key switch
        {
            VirtualKey.LeftWindows or VirtualKey.RightWindows => 1,
            VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => 2,
            VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => 3,
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => 4,
            _ => 5
        };
    }

    private static string GetKeyDisplayName(VirtualKey key)
    {
        return key switch
        {
            VirtualKey.LeftWindows or VirtualKey.RightWindows => "Win",
            VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => "Ctrl",
            VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => "Alt",
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => "Shift",
            VirtualKey.Space => "Space",
            VirtualKey.Tab => "Tab",
            VirtualKey.Enter => "Enter",
            VirtualKey.Escape => "Esc",
            VirtualKey.Back => "Backspace",
            VirtualKey.Delete => "Delete",
            VirtualKey.Insert => "Insert",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "Page Up",
            VirtualKey.PageDown => "Page Down",
            VirtualKey.Up => "↑",
            VirtualKey.Down => "↓",
            VirtualKey.Left => "←",
            VirtualKey.Right => "→",
            // 数字键（主键盘）
            VirtualKey.Number0 => "0",
            VirtualKey.Number1 => "1",
            VirtualKey.Number2 => "2",
            VirtualKey.Number3 => "3",
            VirtualKey.Number4 => "4",
            VirtualKey.Number5 => "5",
            VirtualKey.Number6 => "6",
            VirtualKey.Number7 => "7",
            VirtualKey.Number8 => "8",
            VirtualKey.Number9 => "9",
            // 数字小键盘
            VirtualKey.NumberPad0 => "NumPad 0",
            VirtualKey.NumberPad1 => "NumPad 1",
            VirtualKey.NumberPad2 => "NumPad 2",
            VirtualKey.NumberPad3 => "NumPad 3",
            VirtualKey.NumberPad4 => "NumPad 4",
            VirtualKey.NumberPad5 => "NumPad 5",
            VirtualKey.NumberPad6 => "NumPad 6",
            VirtualKey.NumberPad7 => "NumPad 7",
            VirtualKey.NumberPad8 => "NumPad 8",
            VirtualKey.NumberPad9 => "NumPad 9",
            // 功能键
            VirtualKey.F1 => "F1",
            VirtualKey.F2 => "F2",
            VirtualKey.F3 => "F3",
            VirtualKey.F4 => "F4",
            VirtualKey.F5 => "F5",
            VirtualKey.F6 => "F6",
            VirtualKey.F7 => "F7",
            VirtualKey.F8 => "F8",
            VirtualKey.F9 => "F9",
            VirtualKey.F10 => "F10",
            VirtualKey.F11 => "F11",
            VirtualKey.F12 => "F12",
            // 字母键 - 单独处理以确保正确显示
            >= VirtualKey.A and <= VirtualKey.Z => ((char)key).ToString(),
            _ => key.ToString()
        };
    }

    public event EventHandler? HotkeyChanged;

    private void OnHotkeyChanged()
    {
        HotkeyChanged?.Invoke(this, EventArgs.Empty);
    }

    // 获取下拉选项
    public List<HotkeyOption> DropdownOptions => GetDropdownOptions();

    private List<HotkeyOption> GetDropdownOptions()
    {
        var options = new List<HotkeyOption>();

        // 修饰键
        options.Add(new HotkeyOption("Win", (int)VirtualKey.LeftWindows));
        options.Add(new HotkeyOption("Ctrl", (int)VirtualKey.Control));
        options.Add(new HotkeyOption("Alt", (int)VirtualKey.Menu));
        options.Add(new HotkeyOption("Shift", (int)VirtualKey.Shift));

        // 功能键
        for (var i = 1; i <= 12; i++) // 只到F12，更常见的功能键
        {
            var key = (VirtualKey)(111 + i); // F1-F12
            if (Enum.IsDefined(typeof(VirtualKey), key))
            {
                options.Add(new HotkeyOption($"F{i}", (int)key));
            }
        }

        // 数字键（主键盘）- 优先级最高
        for (var i = 0; i < 10; i++)
        {
            var key = (VirtualKey)('0' + i);
            options.Add(new HotkeyOption(((char)('0' + i)).ToString(), (int)key));
        }

        // 字母键
        for (var i = 0; i < 26; i++)
        {
            var key = (VirtualKey)('A' + i);
            options.Add(new HotkeyOption(((char)('A' + i)).ToString(), (int)key));
        }

        // 特殊键
        options.Add(new HotkeyOption("Space", (int)VirtualKey.Space));
        options.Add(new HotkeyOption("Tab", (int)VirtualKey.Tab));
        options.Add(new HotkeyOption("Enter", (int)VirtualKey.Enter));
        options.Add(new HotkeyOption("Esc", (int)VirtualKey.Escape));
        options.Add(new HotkeyOption("Backspace", (int)VirtualKey.Back));
        options.Add(new HotkeyOption("Delete", (int)VirtualKey.Delete));
        options.Add(new HotkeyOption("Insert", (int)VirtualKey.Insert));
        options.Add(new HotkeyOption("Home", (int)VirtualKey.Home));
        options.Add(new HotkeyOption("End", (int)VirtualKey.End));
        options.Add(new HotkeyOption("Page Up", (int)VirtualKey.PageUp));
        options.Add(new HotkeyOption("Page Down", (int)VirtualKey.PageDown));
        options.Add(new HotkeyOption("↑", (int)VirtualKey.Up));
        options.Add(new HotkeyOption("↓", (int)VirtualKey.Down));
        options.Add(new HotkeyOption("←", (int)VirtualKey.Left));
        options.Add(new HotkeyOption("→", (int)VirtualKey.Right));

        // 数字小键盘 - 优先级较低
        options.Add(new HotkeyOption("NumPad 0", (int)VirtualKey.NumberPad0));
        options.Add(new HotkeyOption("NumPad 1", (int)VirtualKey.NumberPad1));
        options.Add(new HotkeyOption("NumPad 2", (int)VirtualKey.NumberPad2));
        options.Add(new HotkeyOption("NumPad 3", (int)VirtualKey.NumberPad3));
        options.Add(new HotkeyOption("NumPad 4", (int)VirtualKey.NumberPad4));
        options.Add(new HotkeyOption("NumPad 5", (int)VirtualKey.NumberPad5));
        options.Add(new HotkeyOption("NumPad 6", (int)VirtualKey.NumberPad6));
        options.Add(new HotkeyOption("NumPad 7", (int)VirtualKey.NumberPad7));
        options.Add(new HotkeyOption("NumPad 8", (int)VirtualKey.NumberPad8));
        options.Add(new HotkeyOption("NumPad 9", (int)VirtualKey.NumberPad9));

        // 鼠标按键
        options.Add(new HotkeyOption("MOUSE_LEFT", 1));
        options.Add(new HotkeyOption("MOUSE_RIGHT", 2));
        options.Add(new HotkeyOption("MOUSE_MIDDLE", 3));
        options.Add(new HotkeyOption("MOUSE_X1", 4));
        options.Add(new HotkeyOption("MOUSE_X2", 5));
        options.Add(new HotkeyOption("WHEEL_UP", 6));
        options.Add(new HotkeyOption("WHEEL_DOWN", 7));

        return options;
    }

    private void OnDropdownSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (sender is ComboBox comboBox && comboBox.SelectedItem is HotkeyOption selectedOption)
            {
                // 确保选择的选项有效且不是初始化过程
                if (selectedOption.KeyCode >= 0 && InputMode == InputMode.Dropdown) // 有效按键码且在下拉模式下
                {
                    // 防止重复设置相同值
                    if (HotkeyKeys.Count == 0 || HotkeyKeys[0] != selectedOption.KeyCode)
                    {
                        HotkeyKeys = new List<int> { selectedOption.KeyCode };
                        OnHotkeyChanged();
                        // 不需要手动调用UpdateDisplay，因为OnHotkeyKeysChanged会处理显示更新
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 记录异常但不中断程序
            System.Diagnostics.Debug.WriteLine($"OnDropdownSelectionChanged error: {ex.Message}");
        }
    }

    private void UpdateDropdownSelection()
    {
        try
        {
            if (KeyDropdown != null && HotkeyKeys.Count > 0)
            {
                var firstKey = HotkeyKeys[0];
                var matchingOption = DropdownOptions.FirstOrDefault(opt => opt.KeyCode == firstKey);

                // 使用异步方式设置选择，确保UI已完全加载
                KeyDropdown.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        // 临时移除事件处理器以防止触发不必要的事件
                        KeyDropdown.SelectionChanged -= OnDropdownSelectionChanged;

                        if (matchingOption != null)
                        {
                            // 确保在下拉列表中找到匹配项后再设置选择
                            var itemIndex = DropdownOptions.IndexOf(matchingOption);
                            if (itemIndex >= 0 && itemIndex < KeyDropdown.Items.Count)
                            {
                                KeyDropdown.SelectedIndex = itemIndex;
                            }
                            else
                            {
                                KeyDropdown.SelectedIndex = -1;
                            }
                        }
                        else
                        {
                            // 如果没有找到匹配项，清除选择
                            KeyDropdown.SelectedIndex = -1;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"UpdateDropdownSelection (inner) error: {ex.Message}");
                    }
                    finally
                    {
                        // 重新添加事件处理器
                        KeyDropdown.SelectionChanged += OnDropdownSelectionChanged;
                    }
                });
            }
            else if (KeyDropdown != null)
            {
                // 如果没有按键，清除选择
                KeyDropdown.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        KeyDropdown.SelectionChanged -= OnDropdownSelectionChanged;
                        KeyDropdown.SelectedIndex = -1;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"UpdateDropdownSelection (empty) error: {ex.Message}");
                    }
                    finally
                    {
                        KeyDropdown.SelectionChanged += OnDropdownSelectionChanged;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            // 记录异常但不中断程序
            System.Diagnostics.Debug.WriteLine($"UpdateDropdownSelection error: {ex.Message}");
        }
    }

    private string GetDisplayNameForKeyCode(int keyCode)
    {
        // 首先检查是否是鼠标按键
        if (IsMouseKeyCode(keyCode))
        {
            return GetMouseDisplayName(keyCode);
        }

        // 然后检查是否是键盘按键
        if (Enum.IsDefined(typeof(VirtualKey), keyCode))
        {
            var virtualKey = (VirtualKey)keyCode;
            return GetKeyDisplayName(virtualKey);
        }

        // 如果都不匹配，返回未知
        return $"Unknown Key ({keyCode})";
    }

    private void EnsureDropdownInitialized()
    {
        try
        {
            // 确保下拉列表已正确初始化
            if (KeyDropdown != null && DropdownOptions.Count > 0)
            {
                // 强制刷新下拉列表项
                KeyDropdown.ItemsSource = null;
                KeyDropdown.ItemsSource = DropdownOptions;

                // 确保没有默认选择
                KeyDropdown.SelectedIndex = -1;

                // 如果有现有按键，更新选择
                UpdateDropdownSelection();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"EnsureDropdownInitialized error: {ex.Message}");
        }
    }

    
    
    // 辅助方法
    private static bool IsMouseKeyCode(int keyCode) => keyCode switch
    {
        >= 1 and <= 7 => true, // 鼠标按键（包括滚轮上下6,7）
        _ => false
    };

    private static InputMode? FindParentInputMode(DependencyObject child)
    {
        var parent = VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            // 使用反射查找 InputMode 属性
            var inputModeProp = parent.GetType().GetProperty("InputMode");
            if (inputModeProp?.PropertyType == typeof(InputMode))
            {
                return (InputMode?)inputModeProp.GetValue(parent);
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private static string GetMouseDisplayName(int mouseCode) => mouseCode switch
    {
        1 => "MOUSE_LEFT",
        2 => "MOUSE_RIGHT",
        3 => "MOUSE_MIDDLE",
        4 => "MOUSE_X1",
        5 => "MOUSE_X2",
        6 => "WHEEL_UP",
        7 => "WHEEL_DOWN",
        _ => "MOUSE_UNKNOWN"
    };
}

public class HotkeyOption
{
    public string DisplayName { get; set; }
    public int KeyCode { get; set; }
    public bool IsSeparator { get; set; }

    public HotkeyOption(string displayName, int keyCode, bool isSeparator = false)
    {
        DisplayName = displayName;
        KeyCode = keyCode;
        IsSeparator = isSeparator;
    }

    public override string ToString()
    {
        return DisplayName;
    }
}