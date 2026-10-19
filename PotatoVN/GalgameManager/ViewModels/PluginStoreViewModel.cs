using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.WinUI.Collections;
using GalgameManager.Contracts.Services;
using GalgameManager.Contracts.ViewModels;
using GalgameManager.Enums;
using GalgameManager.Helpers;
using GalgameManager.Models;
using GalgameManager.Models.BgTasks;
using GalgameManager.Views.Dialog;
using Microsoft.UI.Xaml.Controls;

namespace GalgameManager.ViewModels;

public partial class PluginStoreViewModel(
    IInfoService infoService,
    IBgTaskService bgTaskService,
    IPluginService pluginService,
    ILocalSettingsService settingsService)
    : ObservableRecipient, INavigationAware
{
    public AdvancedCollectionView Plugins { get; } = new();
    // ReSharper disable once CollectionNeverQueried.Global
    public readonly ObservableCollection<PluginTypeViewModel> PluginTypes = [];
    [ObservableProperty] private PluginTypeViewModel _selectedPluginType = null!;

    public void OnNavigatedTo(object parameter)
    {
        foreach (PluginType type in PluginTypeHelper.GetAllTypes())
            PluginTypes.Add(new()
            {
                Type = type, Title = type.GetLocalized(), Icon = new FontIcon { Glyph = type.ToGlyph() }
            });
        bgTaskService.AddBgTask(new GetStorePluginTask(Plugins));
        SelectedPluginType = PluginTypes.FirstOrDefault(p => p.Type == PluginType.All) ?? PluginTypes.First();
    }

    public void OnNavigatedFrom() {}

    [RelayCommand]
    private async Task ItemClickAsync(StorePlugin? clickedItem)
    {
        try
        {
            if (clickedItem == null) return;
            // infoService.Info(InfoBarSeverity.Success, clickedItem.Name);
            StorePluginDialog dialog = new(clickedItem, pluginService.PluginOffloadInProgress);
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                StorePluginVersion version = dialog.SelectedVersion;
                if ((await pluginService.GetAllPluginsAsync()).Any(p => p.Id == clickedItem.Id))
                {
                    List<ToInstallStorePlugin> list = await settingsService.ReadSettingAsync<List<ToInstallStorePlugin>>
                                                          (KeyValues.ToUpgradePlugin) ?? [];
                    list.Add(new ToInstallStorePlugin{Plugin = clickedItem, Version = version});
                    await settingsService.SaveSettingAsync(KeyValues.ToUpgradePlugin, list);
                    infoService.Info(InfoBarSeverity.Success, "PluginStorePage_UpgradeQueued".GetLocalized());
                    return;
                }
                await bgTaskService.AddBgTask(new InstallStorePluginTask(clickedItem, version));
            }
        }
        catch (Exception e)
        {
            infoService.DeveloperEvent(e: e);
        }
    }

    partial void OnSelectedPluginTypeChanged(PluginTypeViewModel value)
    {
        Plugins.Filter = p =>
        {
            StorePlugin plugin = (StorePlugin)p;
            return value.Type == PluginType.All || plugin.Types.Contains(value.Type);
        };
    }
}

public class PluginTypeViewModel
{
    public string Title = string.Empty;
    public IconElement Icon = new FontIcon() { Glyph = "\uE8EF" };
    public required PluginType Type { get; init; }
}
