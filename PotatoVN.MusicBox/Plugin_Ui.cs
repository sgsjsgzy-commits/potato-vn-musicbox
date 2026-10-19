using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using GalgameManager.WinApp.Base.Models.Plugin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PotatoVN.MusicBox.Controls;
using PotatoVN.MusicBox.Views;

namespace PotatoVN.MusicBox;

public partial class Plugin
{
    private bool _uiInit;

    private void InitUi()
    {
        if (_uiInit) return;

        _hostApi.RegisterSidebarButton(new SidebarButtonInfo
        {
            Id = "open-musicbox",
            Text = "音乐盒",
            Placement = SidebarButtonPlacement.Menu,
            FluentGlyph = "&#xE8D6;", // MusicNote
            FallbackGlyph = "",
        }, () =>
        {
            _hostApi.NavigateTo(typeof(MusicBoxPage), "音乐盒");
            return Task.CompletedTask;
        });

        _uiInit = true;
    }

    public FrameworkElement CreateSettingUi() => new MusicBoxSettingsControl(_data, Library);
}
