using CommunityToolkit.Mvvm.ComponentModel;
using GalgameManager.Enums;

namespace GalgameManager.Models;

/// <summary>
/// 用来展示插件商店里的插件
/// </summary>
public partial class StorePlugin : ObservableObject
{
    /// 插件所在仓库名
    public required string RepoName;
    /// 插件ID
    [ObservableProperty] private Guid _id;
    /// 插件名
    [ObservableProperty] private string _name = string.Empty;
    /// 插件简述
    [ObservableProperty] private string _descriptionShort = string.Empty;
    /// 插件详细描述
    [ObservableProperty] private string _descriptionDetailed = string.Empty;
    [ObservableProperty] private string? _developer;
    [ObservableProperty] private string? _developerUrl;
    [ObservableProperty] private string? _projectUrl;
    /// 插件图标URL
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Logo))] private string? _logoUrl;
    /// 插件图标本地路径
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Logo))] private string? _logoPath;
    public string? Logo => !string.IsNullOrEmpty(LogoPath) ? LogoPath : LogoUrl;
    /// 插件发布日期
    [ObservableProperty] private DateTime _releaseDate;
    /// 插件各个版本与下载链接
    public List<StorePluginVersion> Versions { get; set; } = [];
    /// 插件类别
    public List<PluginType> Types { get; set; } = [];

    [ObservableProperty] private StorePluginStatus _status = StorePluginStatus.NotInstalled;
    [ObservableProperty] private Version? _installedVersion;
}

public enum StorePluginStatus
{
    NotInstalled,
    Installed,
    UpdateAvailable
}

public class StorePluginVersion
{
    public Version Version = new();
    public string DownloadUrl = string.Empty;
    public DateTime ReleaseDate;
}

public class ToInstallStorePlugin
{
    public StorePlugin Plugin = null!;
    public StorePluginVersion Version = null!;
}
