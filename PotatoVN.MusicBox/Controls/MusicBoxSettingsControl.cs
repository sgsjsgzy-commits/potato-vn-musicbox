using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.MusicBox.Models;
using PotatoVN.MusicBox.Services;

namespace PotatoVN.MusicBox.Controls;

/// <summary>
/// Settings shown on the plugin management page: scan scope, duration probing
/// and cache maintenance.
/// </summary>
public sealed class MusicBoxSettingsControl : StackPanel
{
    private readonly PluginData _data;
    private readonly MusicLibraryService _library;

    public MusicBoxSettingsControl(PluginData data, MusicLibraryService library)
    {
        _data = data;
        _library = library;
        Spacing = 12;
        Margin = new Thickness(0, 8, 0, 8);

        Children.Add(SectionHeader("扫描范围"));
        Children.Add(SettingRow(
            "包含语音",
            "把角色语音也加入曲库。语音通常有数千条，会显著增加扫描时间。",
            Toggle(data.IncludeVoice, value => data.IncludeVoice = value)));

        Children.Add(SettingRow(
            "包含音效",
            "把脚步声、界面音等短音效加入曲库。",
            Toggle(data.IncludeSoundEffects, value => data.IncludeSoundEffects = value)));

        Children.Add(SettingRow(
            "读取曲目时长",
            "扫描时读取音频头以显示时长。关闭可加快扫描速度。",
            Toggle(data.ProbeDuration, value => data.ProbeDuration = value)));

        Children.Add(SectionHeader("播放"));
        Children.Add(SettingRow(
            "记住上次播放",
            "重新打开音乐盒时恢复上次选择的游戏与曲目。",
            Toggle(data.RememberPlayback, value => data.RememberPlayback = value)));

        Children.Add(SectionHeader("缓存"));
        Children.Add(SettingRow(
            "缓存位置",
            $"扫描结果按游戏分别保存为 JSON。留空则自动放在游戏所在盘，避免占用系统盘。\n当前：{library.CacheRoot}",
            CacheFolderButton()));
        Children.Add(SettingRow(
            "清除扫描缓存",
            "删除已缓存的曲库数据；下次打开游戏时会重新扫描。",
            ClearCacheButton()));
    }

    /// <summary>
    /// Lets the user point the cache at another folder. The picker runs in the
    /// host window, so it needs the page's XamlRoot, resolved at click time.
    /// </summary>
    private Button CacheFolderButton()
    {
        var button = new Button { Content = "选择…" };
        button.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Plugin.HostApi.GetMainWindow());
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            _data.CacheFolder = folder.Path;
            _library.AdoptLibraryRoot(folder.Path);
            button.Content = "已切换";
            await Task.Delay(1500);
            button.Content = "选择…";
        };
        return button;
    }

    private static TextBlock SectionHeader(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            Margin = new Thickness(0, 8, 0, 0),
        };
        TryApplyStyle(block, "SubtitleTextBlockStyle");
        return block;
    }

    private static Grid SettingRow(string title, string description, FrameworkElement control)
    {
        var left = new StackPanel { Spacing = 2 };
        left.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        var desc = new TextBlock
        {
            Text = description,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 560,
        };
        TryApplyStyle(desc, "DescriptionTextStyle");
        left.Children.Add(desc);

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(left);
        Grid.SetColumn(left, 0);

        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        Grid.SetColumn(control, 1);

        return grid;
    }

    private static ToggleSwitch Toggle(bool initial, Action<bool> onChanged)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = initial,
            OnContent = "开",
            OffContent = "关",
            MinWidth = 0,
        };

        toggle.Toggled += (_, _) => onChanged(toggle.IsOn);
        return toggle;
    }

    private Button ClearCacheButton()
    {
        var button = new Button { Content = "清除" };
        button.Click += async (_, _) =>
        {
            _library.ClearAll();
            button.Content = "已清除";
            await Task.Delay(1500);
            button.Content = "清除";
        };
        return button;
    }

    private static void TryApplyStyle(FrameworkElement element, string key)
    {
        if (Application.Current?.Resources is { } resources
            && resources.TryGetValue(key, out var value)
            && value is Style style)
        {
            element.Style = style;
        }
    }
}
