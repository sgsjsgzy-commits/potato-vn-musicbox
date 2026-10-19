using GalgameManager.ViewModels;

using Microsoft.UI.Xaml.Controls;

namespace GalgameManager.Views;

public sealed partial class PluginStorePage : Page
{
    public PluginStoreViewModel ViewModel
    {
        get;
    }

    public PluginStorePage()
    {
        ViewModel = App.GetService<PluginStoreViewModel>();
        DataContext = ViewModel;
        InitializeComponent();
    }
}
