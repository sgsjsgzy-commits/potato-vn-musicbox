#pragma warning disable MVVMTK0049 //警告INotifyPropertyChanged无法NativeAOT，等后续处理

using CommunityToolkit.Mvvm.ComponentModel;
using GalgameManager.Contracts.Services;
using GalgameManager.Enums;
using GalgameManager.Helpers;
using GalgameManager.Helpers.Converter;
using GalgameManager.Models;
using GalgameManager.Models.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GalgameManager.Views.Dialog;

[INotifyPropertyChanged]
public sealed partial class ChangeSourceDialog
{
    public List<GalgameSourceBase> Sources { get; }
    public List<GalgameSourceBase> GalgameSources { get; }
    public string TargetPath => _targetPath;
    public GalgameSourceBase MoveInSource => Sources[SelectSourceIndex];
    public GalgameSourceBase? MoveOutSource => RemoveFromSource ? GalgameSources![RemoveFromSourceIndex] : null;
    
    [ObservableProperty] private int _selectSourceIndex;
    [ObservableProperty] private Visibility _spacePanelVisibility = Visibility.Collapsed;
    [ObservableProperty] private string _spaceInfo = string.Empty;
    [ObservableProperty] private int _spacePercent;
    [ObservableProperty] private bool _spaceShowError;
    [ObservableProperty] private bool _removeFromSource;
    [ObservableProperty] private int _removeFromSourceIndex;
    [ObservableProperty] private Visibility _removePanelVisibility = Visibility.Collapsed;
    [ObservableProperty] private string _moveInDescription = string.Empty;
    [ObservableProperty] private string? _moveOutDescription;
    [ObservableProperty] private Visibility _operatePanelDescriptionVisibility = Visibility.Collapsed;
    [ObservableProperty] private string? _warningText;

    private readonly Galgame _game;
    private string _targetPath = string.Empty;
    private (long total, long used) _space;

    public ChangeSourceDialog(Galgame game)
    {
        InitializeComponent();
        RequestedTheme = App.MainWindow?.Content is Microsoft.UI.Xaml.FrameworkElement element ? element.RequestedTheme : RequestedTheme;
        XamlRoot = App.MainWindow!.Content.XamlRoot;
        PrimaryButtonText = "Yes".GetLocalized();
        IsPrimaryButtonEnabled = false;
        CloseButtonText = "Cancel".GetLocalized();
        DefaultButton = ContentDialogButton.Close;

        _game = game;
        IGalgameSourceCollectionService sourceCollectionService = App.GetService<IGalgameSourceCollectionService>();
        Sources = sourceCollectionService.GetGalgameSources().ToList();
        foreach (GalgameSourceBase s in _game.Sources)
            Sources.Remove(s);

        GalgameSources = _game.Sources.ToList();
    }

    async partial void OnSelectSourceIndexChanged(int value)
    {
        try
        {
            IsPrimaryButtonEnabled = false;
            _space = (-1, -1);
            Update();
            GalgameSourceBase selectedSource = Sources[value];
            _targetPath = selectedSource.Path;
            IGalgameSourceService service = SourceServiceFactory.GetSourceService(selectedSource.SourceType);
            // 空间
            SpacePanelVisibility = Visibility.Collapsed;
            _space = await service.GetSpaceAsync(selectedSource);
            Update();
        }
        catch (Exception exception)
        {
            App.GetService<IInfoService>().Event(EventType.GalgameEvent, InfoBarSeverity.Error,
                "Error during getting addition setting control", exception);
            Hide();
        }
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnRemoveFromSourceChanged(bool value) => Update();

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnRemoveFromSourceIndexChanged(int value) => Update();

    private void Update()
    {
        //容量相关
        if (_space.total != -1 && _space.used != -1)
        {
            SpacePercent = (int)(_space.used * 100 / _space.total);
            SpaceShowError = SpacePercent >= 90;
            SpaceInfo = "ChangeSourceDialog_Space".GetLocalized(
                CapacityToStringConverter.Convert(_space.total - _space.used),
                CapacityToStringConverter.Convert(_space.total));
            SpacePanelVisibility = Visibility.Visible;
        }
        //警告文本及判断是否允许点击确定按钮
        if (Sources.Count > 0)
        {
            WarningText = SourceServiceFactory.GetSourceService(MoveInSource.SourceType)
                .CheckMoveOperateValid(MoveInSource, MoveOutSource, _game);
            IsPrimaryButtonEnabled = WarningText is null;
        }
        //移出源面板相关
        RemovePanelVisibility = (GalgameSources.Count > 0).ToVisibility() ;
        //操作提示面板相关
        OperatePanelDescriptionVisibility = IsPrimaryButtonEnabled.ToVisibility();
        GalgameSourceBase selectedSource = Sources[SelectSourceIndex];
        MoveInDescription = SourceServiceFactory.GetSourceService(selectedSource.SourceType)
            .GetMoveInDescription(selectedSource, _targetPath);
        if (RemoveFromSource)
        {
            GalgameSourceBase selectedMoveOutSource = GalgameSources[RemoveFromSourceIndex];
            MoveOutDescription = SourceServiceFactory.GetSourceService(selectedMoveOutSource.SourceType)
                .GetMoveOutDescription(selectedMoveOutSource, _game);
        }
        else
            MoveOutDescription = null;
    }
}

#pragma warning restore MVVMTK0049 //警告INotifyPropertyChanged无法NativeAOT，等后续处理