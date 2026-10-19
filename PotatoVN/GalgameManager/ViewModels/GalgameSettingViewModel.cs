using System.ComponentModel;
using Windows.Storage;
using Windows.Storage.Pickers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GalgameManager.Contracts.Services;
using GalgameManager.Contracts.ViewModels;
using GalgameManager.Enums;
using GalgameManager.Helpers;
using GalgameManager.Helpers.Converter;
using GalgameManager.Helpers.EnumHelpers;
using GalgameManager.Models;
using System.Collections.ObjectModel;
using GalgameManager.Core.Helpers;
using GalgameManager.Services;
using GalgameManager.Models.Sources;
using GalgameManager.Views.Dialog;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GalgameManager.ViewModels;

public partial class GalgameSettingViewModel : ObservableObject, INavigationAware, IRecipient<GalgameParsingEventArgs>
{
    [ObservableProperty]
    private Galgame _gal = null!;

    public List<RssType> RssTypes { get; } = [];

    private readonly GalgameCollectionService _galService;
    private readonly GalgameSourceCollectionService _sourceService;
    private readonly INavigationService _navigationService;
    private readonly IPvnService _pvnService;
    private readonly IInfoService _infoService;
    private readonly ILocalSettingsService _settingsService;
    private readonly IMessenger _bus;
    private readonly Dictionary<int, string> _searchUrlList = [];
    [ObservableProperty] private string _searchUri = "";
    [ObservableProperty] private bool _isPhrasing;
    [ObservableProperty] private string _parsingMsg = string.Empty;
    [ObservableProperty] private RssType _selectedRss = RssType.None;
    [ObservableProperty] private string _galgameInfoDescription = string.Empty;
    [ObservableProperty] private ObservableCollection<KeyMapping> _keyMappings = new();
    /// <summary>
    /// 当前逻辑游戏安装实例的界面集合，仅供界面读取。
    /// </summary>
    public ObservableCollection<GalgameAndPath> Installations { get; } = [];
    [ObservableProperty] private GalgameAndPath? _selectedInstallation; // 设置页当前选中的安装实例
    /// <summary>
    /// 当前选中安装实例的配置。
    /// </summary>
    public LocalInstallationConfig? SelectedInstallationConfig => SelectedInstallation?.LocalConfig;
    [ObservableProperty] private DateTimeOffset _releasedDate; //包一层的原因：CalendarDatePicker的Date为DateTimeOffset（而非datetime）
    [ObservableProperty] private double _tagWidth = 20; //没法设置Expander为Stretch，故暂直接设置宽度
    public string ExePathMsg => SelectedInstallationConfig?.ExePath ?? "GalgameSettingPage_NoExe".GetLocalized();
    public bool IsLocalGame => Gal.IsLocalGame;
    public string SavePositionDescription =>
        SelectedInstallationConfig?.DetectedSavePath?.ToDisplay()
        ?? "GalgameSettingPage_DetectedSavePosition".GetLocalized();

    public GalgameSettingViewModel(IGalgameCollectionService galCollectionService, INavigationService navigationService,
        IPvnService pvnService, IInfoService infoService, IGalgameSourceCollectionService sourceService,
        ILocalSettingsService settingsService, IMessenger bus)
    {
        Gal = new Galgame();
        _galService = (GalgameCollectionService)galCollectionService;
        _sourceService = (GalgameSourceCollectionService)sourceService;
        _navigationService = navigationService;
        _pvnService = pvnService;
        _infoService = infoService;
        _settingsService = settingsService;
        _bus = bus;
        _searchUrlList[(int)RssType.Bangumi] = "https://bgm.tv/subject_search/";
        _searchUrlList[(int)RssType.Vndb] = "https://vndb.org/v/all?sq=";
        _searchUrlList[(int)RssType.Mixed] = "https://bgm.tv/subject_search/";
        _searchUrlList[(int)RssType.Ymgal] = "https://www.ymgal.games/search?type=ga&keyword=";
        _searchUrlList[(int)RssType.Cngal] = "https://www.cngal.org/search?Types=Game&Text=";
        _searchUrlList[(int)RssType.Hikarinagi] = "https://www.hikarinagi.org/search?types=galgame&q=";
        SearchUri = _searchUrlList[(int)RssType.Vndb]; // default
        foreach (RssType type in RssHelperX.GetAvailableTypes(_galService)) RssTypes.Add(type);
    }

    public async void OnNavigatedFrom()
    {
        Gal.KeyMappings = new List<KeyMapping>(KeyMappings);
        if (Gal.ImagePath.Value != Galgame.DefaultImagePath && !File.Exists(Gal.ImagePath.Value))
            Gal.ImagePath.Value = Galgame.DefaultImagePath;
        foreach (GalgameSourceBase source in Installations.Select(i => i.Source).OfType<GalgameSourceBase>().Distinct())
            _sourceService.Save(source);
        await _galService.SaveGalgameAsync(Gal);
        _pvnService.Upload(Gal, PvnUploadProperties.Infos | PvnUploadProperties.ImageLoc);
        Gal.PropertyChanged -= HandleGalPropertyChanged;
        _bus.Unregister<GalgameParsingEventArgs>(this);
    }

    public async void OnNavigatedTo(object parameter)
    {
        if (parameter is not Galgame galgame)
        {
            return;
        }

        Gal = galgame;
        RefreshInstallations();
        KeyMappings = new ObservableCollection<KeyMapping>();

        // 用户设置优先：先导入用户的快捷键设置
        List<KeyMapping> userMappings = Gal.KeyMappings.ToList();
        List<KeyMapping> globalMappings = await GetGlobalKeyMappingsAsync();

        // 先添加用户的所有快捷键设置
        foreach (var userMapping in userMappings)
        {
            KeyMappings.Add(userMapping);
        }

        // 然后处理全局快捷键，只添加用户没有定义的
        foreach (KeyMapping globalMapping in globalMappings)
        {
            // 检查用户是否已经定义了这个按键（通过From键匹配）
            var hasUserMapping = userMappings.Any(um =>
                um.From != null && globalMapping.From != null &&
                um.From.SequenceEqual(globalMapping.From));

            if (!hasUserMapping)
            {
                // 用户没有定义这个按键，添加全局设置到头部
                KeyMappings.Insert(0, new KeyMapping
                {
                    From = new List<int>(globalMapping.From),
                    To = globalMapping.To != null ? new List<int>(globalMapping.To) : new List<int>(),
                    Remark = globalMapping.Remark,
                    IsGlobal = true,
                    IsEnabled = true
                });
            }
        }

        Gal.PropertyChanged += HandleGalPropertyChanged;
        SelectedRss = Gal.RssType;
        if (Gal.ReleaseDate.Value > DateTime.MinValue)
            ReleasedDate = Gal.ReleaseDate.Value;
        _bus.Register(this);
        Update();
    }

    private void RefreshInstallations(Guid? selectedId = null)
    {
        selectedId ??= SelectedInstallation?.EntryId ?? Gal.PreferredInstallationId;
        Installations.Clear();
        foreach (GalgameAndPath installation in Gal.LocalInstallations)
            Installations.Add(installation);
        SelectedInstallation = Installations.FirstOrDefault(i => i.EntryId == selectedId)
                               ?? Installations.FirstOrDefault();
        OnPropertyChanged(nameof(Installations));
        RefreshInstallationBindings();
    }

    private void RefreshInstallationBindings()
    {
        OnPropertyChanged(nameof(SelectedInstallationConfig));
        OnPropertyChanged(nameof(ExePathMsg));
        OnPropertyChanged(nameof(IsLocalGame));
        OnPropertyChanged(nameof(SavePositionDescription));
    }

    partial void OnSelectedInstallationChanged(GalgameAndPath? value) => RefreshInstallationBindings();

    partial void OnSelectedRssChanged(RssType value)
    {
        Gal.RssType = value;
        if (!string.IsNullOrEmpty(_searchUrlList.GetValueOrDefault((int)value)))
            SearchUri = _searchUrlList[(int)value] + Gal.Name.Value;
    }

    [RelayCommand]
    private void OnBack()
    {
        if(_navigationService.CanGoBack)
        {
            _navigationService.GoBack();
        }
    }

    [RelayCommand]
    private async Task OnGetInfoFromRss(object parameter)
    {
        IsPhrasing = true;
        // 检查是否是 isNameOnly 模式
        if (parameter is string isNameOnly && isNameOnly == "True")
        {
            // 清除目前存储的id信息
            for (var i = 0; i < Galgame.PhraserNumber; i++)
            {
                // 跳过PotatoVn
                if (i == (int)RssType.PotatoVn)
                    continue;
                Gal.Ids[i] = null;
            }
        }

        try
        {
            await _galService.ParseGalInfoAsync(Gal, Gal.RssType);
        }
        catch (Exception e)
        {
            _infoService.Info(InfoBarSeverity.Error, "GalgameSettingPage_GetInfoFromRssFailed".GetLocalized(),
                e.Message);
            _infoService.Log(InfoBarSeverity.Error, $"{e.Message}\n{e.StackTrace}");
        }
        finally
        {
            IsPhrasing = false;
            Update();
        }
    }

    private void Update()
    {
        GalgameInfoDescription = $"{"GalgameSettingPage_GameInfo_SettingDescription".GetLocalized()}" +
                                 $"   |    {"GalgameSettingPage_LastFetchInfoTime".GetLocalized(
                                     new DateTimeToStringConverter().Convert(Gal.LastFetchInfoTime, null!, null!, null!))}";
    }

    private void HandleGalPropertyChanged(object? sender, PropertyChangedEventArgs propertyChangedEventArgs)
    {
        RefreshInstallationBindings();
    }

    [RelayCommand]
    private async Task PickImageAsync()
    {
        var newFile = await DownloadHelper.PickImageAsync();
        if (newFile is null) return;
        Gal.ImagePath.Value= newFile;
    }

    [RelayCommand]
    private async Task PickHeaderImageAsync()
    {
        var newFile = await DownloadHelper.PickImageAsync();
        if (newFile is null) return;
        await SetHeaderImgAsync(newFile, null, false);
        _infoService.Info(InfoBarSeverity.Success, "SettingSuccess".GetLocalized());
    }

    [RelayCommand]
    private async Task PickImageFromRssAsync(object? parameter)
    {
        GameParseType parseType = parameter is string typeStr && Enum.TryParse(typeStr, out GameParseType pt)
            ? pt : GameParseType.Image;

        try
        {
            IsPhrasing = true;
            List<string> imageUrls = await _galService.ParserGalImagesAsync(Gal, parseType);
            IsPhrasing = false;

            if (imageUrls.Count == 0)
            {
                _infoService.Info(InfoBarSeverity.Warning, "GalgameSettingPage_NoImagesFound".GetLocalized());
                return;
            }

            ImagePickerDialog dialog = new(imageUrls, parseType)
            {
                XamlRoot = App.MainWindow!.Content.XamlRoot
            };
            dialog.Resources["ContentDialogMaxWidth"] = App.MainWindow.Bounds.Width * 0.8;
            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary || string.IsNullOrEmpty(dialog.SelectedImageUrl)) return;

            IsPhrasing = true;
            if (parseType == GameParseType.HeaderImage) await SetHeaderImgAsync(null, dialog.SelectedImageUrl);
            else await SetImgAsync(null, dialog.SelectedImageUrl);

            await _galService.SaveGalgameAsync(Gal);
            _infoService.Info(InfoBarSeverity.Success, "GalgameSettingPage_ImageSaved".GetLocalized());
        }
        catch (Exception e)
        {
            if (e is PvnException pvnE)
                _infoService.Info(InfoBarSeverity.Error, pvnE.Message);
            else
                _infoService.DeveloperEvent(e: e);
        }
        finally
        {
            IsPhrasing = false;
        }
    }

    [RelayCommand]
    private async Task PickImageFromClipboardAsync(object? parameter)
    {
        GameParseType parseType = parameter is string typeStr && Enum.TryParse(typeStr, out GameParseType pt)
            ? pt : GameParseType.Image;

        var isHeader = parseType == GameParseType.HeaderImage;
        var timestamp = DateTime.Now.ToUnixTime();

        try
        {
            var tempName = $"{Gal.Name.Value}_{timestamp}_clipboard_tmp".RemoveInvalidChars();
            var tempPath = await DownloadHelper.TrySaveClipboardImageAsPngAsync(tempName);
            if (tempPath is null) throw new PvnException("GalgameSettingPage_ClipboardNotImage".GetLocalized());
            if (isHeader)
                await SetHeaderImgAsync(tempPath, null);
            else
                await SetImgAsync(tempPath, null, false);
        }
        catch (Exception e)
        {
            _infoService.Info(InfoBarSeverity.Error, "GalgameSettingPage_ClipboardReadFailed".GetLocalized(), e.Message);
        }
    }

    private async Task SetHeaderImgAsync(string? imagePath, string? imgUrl, bool deletePathImg = true)
    {
        string? tempFile = null; //如果是从URL下载的图片，tempFile用于存储下载的临时文件路径
        var processing = IsPhrasing;
        try
        {
            IsPhrasing = true;
            if (string.IsNullOrEmpty(imagePath) && string.IsNullOrEmpty(imgUrl))
                throw new PvnException("Both imagePath and imgUrl are null or empty"); //不应该发生
            if (!string.IsNullOrEmpty(imagePath) && !File.Exists(imagePath))
                throw new FileNotFoundException("Specified image file not found", imagePath); //不应该发生

            if (imgUrl is not null)
            {
                tempFile = await DownloadHelper.DownloadAndSaveImageWithDiffThread(imgUrl,
                    fileNameWithoutExtension: DateTime.Now.ToUnixTime().ToString());
                imagePath = tempFile;
            }

            if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                throw new PvnException("DownloadImageFailed".GetLocalized());
            DownloadHelper.DeleteImgIfExists(Gal.HeaderImagePath.Value);
            Gal.HeaderImagePath.Value = null;
            var targetPath = Path.Combine((await FileHelper.GetFolderAsync(FileHelper.FolderType.Images)).Path,
                $"{Gal.Name.Value}_Header_{DateTime.Now.ToUnixTime()}.png");
            await Task.Run(() =>
            {
                DownloadHelper.ProcessImage(imagePath, targetPath, false);
            });
            Gal.HeaderImagePath.Value = targetPath;
            Gal.HeaderImageUrl = imgUrl; //若imgUrl不为空，说明是从网络获取的图片，优先使用URL以便同步服务上传；若imgUrl为空，同步系统会上传选择图片
            if (await _settingsService.ReadSettingAsync<bool>(KeyValues.SyncGames) &&
                await _settingsService.ReadSettingAsync<bool>(KeyValues.SyncHeaderImage))
                Gal.PvnUploadProperties |= PvnUploadProperties.HeaderImageLoc;
        }
        finally
        {
            IsPhrasing = processing;
            DownloadHelper.DeleteImgIfExists(tempFile);
            if (deletePathImg)
                DownloadHelper.DeleteImgIfExists(imagePath);
        }
    }

    private async Task SetImgAsync(string? imagePath, string? imgUrl, bool deletePathImg = true)
    {
        if (string.IsNullOrEmpty(imagePath) && string.IsNullOrEmpty(imgUrl))
            throw new PvnException("Both imagePath and imgUrl are null or empty"); //不应该发生
        if (!File.Exists(imagePath) && string.IsNullOrEmpty(imgUrl))
            throw new PvnException($"{imagePath} is not found");
        try
        {
            var tmp = imagePath;
            if (!string.IsNullOrEmpty(imgUrl))
                tmp = await DownloadHelper.DownloadAndSaveImageWithDiffThread(imgUrl,
                    fileNameWithoutExtension: $"{Gal.Name.Value}_{DateTime.Now.ToUnixTime()}_cover");
            if (!File.Exists(tmp)) throw new PvnException("DownloadImageFailed".GetLocalized());
            DownloadHelper.DeleteImgIfExists(Gal.ImagePath.Value);
            Gal.ImagePath.Value = tmp;
            Gal.PvnUploadProperties |= PvnUploadProperties.ImageLoc;
        }
        finally
        {
            if (deletePathImg)
                DownloadHelper.DeleteImgIfExists(imagePath);
        }
    }

    [RelayCommand]
    private async Task SetGalgamePathAsync()
        => await PickInstallationPathAsync(replaceSelected: true);

    [RelayCommand]
    private async Task AddInstallationAsync()
        => await PickInstallationPathAsync(replaceSelected: false);

    private async Task PickInstallationPathAsync(bool replaceSelected)
    {
        try
        {
            FileOpenPicker openPicker = new();
            WinRT.Interop.InitializeWithWindow.Initialize(openPicker, App.MainWindow!.GetWindowHandle());
            openPicker.ViewMode = PickerViewMode.Thumbnail;
            openPicker.FileTypeFilter.Add(".exe");
            openPicker.FileTypeFilter.Add(".bat");
            openPicker.FileTypeFilter.Add(".EXE");
            StorageFile? file = await openPicker.PickSingleFileAsync();
            if (file is not null)
            {
                string folder = Path.GetDirectoryName(file.Path)!;
                GalgameAndPath? oldInstallation = replaceSelected ? SelectedInstallation : null;
                string sourcePath = Directory.GetParent(folder)?.FullName
                                    ?? throw new PvnException("Unable to determine source path.");
                GalgameSourceBase? existingSource =
                    _sourceService.GetGalgameSource(GalgameSourceType.LocalFolder, sourcePath);

                if (oldInstallation is not null && oldInstallation.Source == existingSource)
                {
                    LocalInstallationConfig config =
                        oldInstallation.LocalConfig?.Relocated(oldInstallation.Path, folder)
                        ?? new LocalInstallationConfig();
                    config.ExePath = file.Path;
                    oldInstallation.Path = folder;
                    oldInstallation.LocalConfig = config;
                    Gal.SetPreferredInstallation(oldInstallation);
                    _sourceService.Save(existingSource!);
                    await _galService.SaveGalgameAsync(Gal);
                    RefreshInstallations(oldInstallation.EntryId);
                    _infoService.Info(InfoBarSeverity.Success,
                        "GalgameSettingPage_PathSetSuccess".GetLocalized());
                    return;
                }

                await _galService.SetLocalPathAsync(Gal, folder);
                GalgameAndPath newInstallation = Gal.LocalInstallations.First(i =>
                    Utils.ArePathsEqual(i.Path, folder));
                LocalInstallationConfig newConfig = oldInstallation?.LocalConfig
                    ?.Relocated(oldInstallation.Path, folder) ?? new LocalInstallationConfig();
                newConfig.ExePath = file.Path;
                newInstallation.LocalConfig = newConfig;
                if (oldInstallation is not null)
                    await _sourceService.MoveOutNoOperate(oldInstallation);
                if (replaceSelected || Gal.PreferredInstallationId is null)
                    Gal.SetPreferredInstallation(newInstallation);
                if (newInstallation.Source is not null) _sourceService.Save(newInstallation.Source);
                await _galService.SaveGalgameAsync(Gal);
                RefreshInstallations(newInstallation.EntryId);
                _infoService.Info(InfoBarSeverity.Success, "GalgameSettingPage_PathSetSuccess".GetLocalized());
            }
        }
        catch (Exception e)
        {
            _infoService.Info(InfoBarSeverity.Error, "GalgameSettingPage_PathSetFailed".GetLocalized(), e.Message);
            _infoService.Log(InfoBarSeverity.Error, $"{e.Message}\n{e.StackTrace}");
        }
    }

    [RelayCommand]
    private async Task SetPreferredInstallation(GalgameAndPath? installation)
    {
        if (installation is null) return;
        Gal.SetPreferredInstallation(installation);
        await _galService.SaveGalgameAsync(Gal);
        RefreshInstallations(installation.EntryId);
    }

    [RelayCommand]
    private async Task RemoveInstallation(GalgameAndPath? installation)
    {
        if (installation is null) return;
        ContentDialog dialog = new()
        {
            XamlRoot = App.MainWindow!.Content.XamlRoot,
            Title = "MultiInstall_Unlink_Title".GetLocalized(),
            Content = "MultiInstall_Unlink_Content".GetLocalized() + $"\n{installation.Path}",
            PrimaryButtonText = "Yes".GetLocalized(),
            CloseButtonText = "Cancel".GetLocalized(),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _sourceService.MoveOutNoOperate(installation);
        RefreshInstallations();
    }

    [RelayCommand]
    private static async Task OpenInstallationFolder(GalgameAndPath? installation)
    {
        if (installation is null || !Directory.Exists(installation.Path)) return;
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(installation.Path);
        await Windows.System.Launcher.LaunchFolderAsync(folder);
    }

    [RelayCommand]
    private async Task SetGalgameExePathAsync()
    {
        if(SelectedInstallation is null)
        {
            _infoService.Info(InfoBarSeverity.Error, "GalgameSettingPage_NotLocalGame".GetLocalized());
            return;
        }
        try
        {
            // 先清空ExePath，再调用函数来设置
            // Gal.ExePath = null;
            var result = await _galService.GetGalgameExeAsync(Gal, SelectedInstallation);
            if (result is null)
            {
                _infoService.Info(InfoBarSeverity.Warning, "GalgameSettingPage_ExePathSetCanceled".GetLocalized());
                return;
            }
            if (SelectedInstallation.Source is not null) _sourceService.Save(SelectedInstallation.Source);
            RefreshInstallationBindings();
            _infoService.Info(InfoBarSeverity.Success, "GalgameSettingPage_ExePathSetSuccess".GetLocalized());
        }
        catch (Exception e)
        {
            _infoService.Info(InfoBarSeverity.Error, "GalgameSettingPage_ExePathSetFailed".GetLocalized(), e.Message);
            _infoService.Log(InfoBarSeverity.Error, $"{e.Message}\n{e.StackTrace}");
        }
    }

    [RelayCommand]
    private async Task SetSavePositionAsync()
    {
        if (SelectedInstallation?.LocalConfig is not { } config) return;
        try
        {
            // 确定起始路径：如果已检测到存档位置则使用它，否则使用 AppData
            string initialPath;
            var absolutePath = config.DetectedSavePath?.ToPath();
            if (!string.IsNullOrEmpty(absolutePath) && Directory.Exists(absolutePath))
            {
                initialPath = absolutePath;
            }
            else
            {
                initialPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }

            // 检查是否已有检测到的存档位置
            var hasDetectedSavePosition = !string.IsNullOrEmpty(config.DetectedSavePath);

            // 尝试打开资源管理器到指定路径，然后让用户选择
            await ShowFolderPickerWithPath(initialPath, hasDetectedSavePosition);
        }
        catch (Exception e)
        {
            _infoService.Info(InfoBarSeverity.Error, "GalgameSettingPage_SavePositionSetFailed".GetLocalized(), e.Message);
            _infoService.Log(InfoBarSeverity.Error, $"{e.Message}\n{e.StackTrace}");
        }
    }

    /// <summary>
    /// 显示文件夹选择器，并打开资源管理器到指定路径
    /// </summary>
    private async Task ShowFolderPickerWithPath(string initialPath, bool hasDetectedSavePosition)
    {
        try
        {
            // 使用自定义的存档位置对话框
            var dialog = new SavePositionDialog(Gal, initialPath, hasDetectedSavePosition);
            await dialog.ShowAsync();

            switch (dialog.Result)
            {
                case SavePositionDialogResult.OpenExplorer:
                    _infoService.Info(InfoBarSeverity.Informational,
                        "已在资源管理器中打开路径，您可以浏览后使用文件夹选择器选择具体位置", displayTimeMs: 3000);

                    // 询问用户是否需要使用文件夹选择器
                    await AskForFolderPickerAfterExplorer();
                    break;

                case SavePositionDialogResult.UseStandardPicker:
                    // 用户选择使用标准选择器
                    await ShowStandardFolderPicker();
                    break;

                case SavePositionDialogResult.Cancel:
                    // 用户取消，什么都不做
                    break;
            }
        }
        catch (Exception ex)
        {
            _infoService.Info(InfoBarSeverity.Warning, "OpenExplorer failed", ex.Message);
            // 回退到标准选择器
            await ShowStandardFolderPicker();
        }
    }

    /// <summary>
    /// 打开资源管理器后询问用户是否需要文件夹选择器
    /// </summary>
    private async Task AskForFolderPickerAfterExplorer()
    {
        var dialog = new FolderPickerConfirmationDialog();
        await dialog.ShowAsync();

        switch (dialog.Result)
        {
            case FolderPickerConfirmationResult.ShowPicker:
                await ShowStandardFolderPicker();
                break;

            case FolderPickerConfirmationResult.Skip:
                // 用户选择不需要，什么都不做
                break;

            case FolderPickerConfirmationResult.Cancel:
                // 用户取消，什么都不做
                break;
        }
    }

    /// <summary>
    /// 显示标准文件夹选择器
    /// </summary>
    private async Task ShowStandardFolderPicker()
    {
        if (SelectedInstallation?.LocalConfig is not { } config) return;
        PvnFolderPicker picker = new()
        {
            Title = "GalgameSettingViewModel_ShowStandardFolderPicker_Title".GetLocalized(),
            OkButtonLabel = "Choose".GetLocalized(),
            InitialDirectory = SelectedInstallation.Path,
        };
        picker.ShowDialog();
        var folder = picker.SelectedPath;

        if (folder is not null)
        {
            config.DetectedSavePath = GamePortablePath.Create(folder, SelectedInstallation.Path);
            if (SelectedInstallation.Source is not null) _sourceService.Save(SelectedInstallation.Source);
            await _galService.SaveGalgameAsync(Gal);
            _infoService.Info(InfoBarSeverity.Success, "GalgameSettingPage_SavePositionUpdated".GetLocalized(), displayTimeMs: 2000);
        }
    }

    partial void OnReleasedDateChanged(DateTimeOffset value)
    {
        if (value.LocalDateTime == Gal.ReleaseDate.Value) return;
        Gal.ReleaseDate.Value = value.LocalDateTime;
    }

    [RelayCommand]
    private void OnPageSizeChanged(SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 65) return;
        TagWidth = e.NewSize.Width - 65;
    }

    public void Receive(GalgameParsingEventArgs message)
    {
        if (message.Galgame.Uuid != Gal.Uuid) return;
        UiThreadInvokeHelper.Invoke(() => ParsingMsg = message.Message);
    }

    [RelayCommand]
    private async Task ReDetectSavePosition()
    {
        if (SelectedInstallation?.LocalConfig is not { } config) return;
        try
        {
            // 清空检测到的存档位置
            config.DetectedSavePath = null;
            if (SelectedInstallation.Source is not null) _sourceService.Save(SelectedInstallation.Source);

            // 保存游戏设置
            await _galService.SaveGalgameAsync(Gal);

            // 显示成功信息
            _infoService.Info(InfoBarSeverity.Success,
                "GalgameSettingPage_ReDetectSuccess".GetLocalized(),
                displayTimeMs: 3000);

            // 刷新UI显示
            OnPropertyChanged(nameof(SavePositionDescription));
        }
        catch (Exception e)
        {
            _infoService.Info(InfoBarSeverity.Error,
                "GalgameSettingPage_ReDetectFailed".GetLocalized(),
                e.Message);
            _infoService.Log(InfoBarSeverity.Error, $"{e.Message}\n{e.StackTrace}");
        }
    }

    [RelayCommand]
    private async Task OpenKeyMappingDialog()
    {
        KeyMappingDialog dialog = new(this)
        {
            XamlRoot = App.MainWindow!.Content.XamlRoot,
            RequestedTheme = App.MainWindow.Content is FrameworkElement element ? element.RequestedTheme : ElementTheme.Default
        };

        ContentDialogResult result = await dialog.ShowAsync();

        // 只有在用户点击保存时才保存设置并显示通知
        if (result == ContentDialogResult.Primary)
        {
            await SaveKeyMappingsAsync();
            _infoService.Info(InfoBarSeverity.Success, msg:"KeyMapping_Info_KeyMappingSaved".GetLocalized(), displayTimeMs: 2000);
        }
    }


    /// <summary>
    /// 检查全局快捷键是否已存在于当前快捷键列表中
    /// </summary>
    /// <param name="globalMapping">要检查的全局快捷键</param>
    /// <returns>如果不存在返回true，存在返回false</returns>
    private bool IsGlobalKeyMappingNotExists(KeyMapping globalMapping)
    {
        return globalMapping.From != null && KeyMappings.All(k => k.From == null || !k.From.SequenceEqual(globalMapping.From));
    }

    /// <summary>
    /// 从全局设置中获取所有全局快捷键
    /// </summary>
    /// <returns>全局快捷键列表</returns>
    private async Task<List<KeyMapping>> GetGlobalKeyMappingsAsync()
    {
        try
        {
            return await _settingsService.ReadSettingAsync<List<KeyMapping>>(KeyValues.GlobalKeyMappings) ?? new();
        }
        catch (Exception e)
        {
            _infoService.DeveloperEvent(e: e);
            return new List<KeyMapping>();
        }
    }


    [RelayCommand]
    private void AddKeyMapping()
    {
        KeyMappings.Add(new KeyMapping { IsGlobal = false });
    }

    [RelayCommand]
    private void RemoveKeyMapping(KeyMapping? mapping)
    {
        if (mapping != null)
        {
            KeyMappings.Remove(mapping);
        }
    }

    /// <summary>
    /// 保存当前游戏的快捷键映射设置
    /// </summary>
    public async Task SaveKeyMappingsAsync()
    {
        // 保存所有映射，包括用户修改过的全局快捷键设置
        // 这样用户对全局快捷键的自定义设置会被保留
        Gal.KeyMappings = new List<KeyMapping>(KeyMappings);

        // 立即保存游戏数据
        await _galService.SaveGalgameAsync(Gal);
    }
}
