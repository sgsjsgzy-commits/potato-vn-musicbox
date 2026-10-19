using CommunityToolkit.Mvvm.Messaging;
using Windows.Storage;
using GalgameManager.Contracts.Services;
using GalgameManager.Enums;
using GalgameManager.Helpers;
using GalgameManager.Models;
using GalgameManager.Models.BgTasks;
using GalgameManager.Models.Sources;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Models.Plugin;
using LiteDB;
using Microsoft.UI.Xaml.Controls;

namespace GalgameManager.Services;

public partial class PluginService
{
    public partial class PotatoVnApiHost(PluginX plugin) : IPotatoVnApi
    {
        private readonly ILiteCollection<PluginData> _pluginDataDb = App.GetService<ILocalSettingsService>()
            .Database.GetCollection<PluginData>("plugin_data");
        private readonly IInfoService _infoService = App.GetService<IInfoService>();
        private readonly IBgTaskService _bgTaskService = App.GetService<IBgTaskService>();
        private readonly IGalgameCollectionService _gameService = App.GetService<IGalgameCollectionService>();
        private readonly ILocalSettingsService _settingService = App.GetService<ILocalSettingsService>();
        private readonly ISidebarService _sidebarService = App.GetService<ISidebarService>();
        private readonly IGameLaunchService _gameLaunchService =
            App.GetService<IGameLaunchService>(); // 为插件按明确安装实例启动游戏

        #region GAMES

        public List<Galgame> GetAllGames() => _gameService.Galgames.ToList();

        /// <inheritdoc />
        public async Task<Galgame> AddGameInstallation(string path, bool force = true, bool requireConfirm = true)
        {
            Galgame? result = await _gameService.AddGameAsync(GalgameSourceType.LocalFolder, path, force, requireConfirm);
            return result ?? throw new InvalidOperationException("Failed to add game.");
        }

        /// <inheritdoc />
        public async Task<Galgame> AddVirtualGame(string name, bool force = true, bool requireConfirm = true)
        {
            Galgame? result = await _gameService.AddGameAsync(GalgameSourceType.Virtual, name, force, requireConfirm);
            return result ?? throw new InvalidOperationException("Failed to add virtual game.");
        }

        /// <inheritdoc />
        public IReadOnlyList<GameInstallationInfo> GetGameInstallations(Galgame game) =>
            game.LocalInstallations.Select(installation => new GameInstallationInfo(
                installation.EntryId,
                installation.Source?.Id ?? Guid.Empty,
                installation.Source?.SourceType ?? GalgameSourceType.UnKnown,
                installation.Source?.Name ?? string.Empty,
                installation.Path,
                game.PreferredInstallationId == installation.EntryId,
                Directory.Exists(installation.Path))).ToList();

        /// <inheritdoc />
        public async Task LaunchGameAsync(Galgame game, Guid? installationId = null)
        {
            GalgameAndPath? installation = installationId is { } id
                ? game.LocalInstallations.FirstOrDefault(i => i.EntryId == id)
                : game.LocalInstallations.FirstOrDefault(i => i.EntryId == game.PreferredInstallationId);
            installation ??= game.LocalInstallations.Count == 1 ? game.LocalInstallations[0] : null;
            if (installation is null)
                throw new InvalidOperationException("No unambiguous local installation is available.");
            await UiThreadInvokeHelper.InvokeAsync(() => _gameLaunchService.LaunchAsync(game, installation));
        }

        #endregion

        #region DATA

        public Task<string?> GetDataAsync()
        {
            //Task包一层，防止调用方直接在UI线程调用
            return Task.Run(() =>
            {
                PluginData? data = _pluginDataDb.FindById(plugin.Info.Id);
                return data?.Data;
            });
        }

        public async Task SaveDataAsync(string data)
        {
            //Task包一层，防止调用方直接在UI线程调用
            await Task.Run(() =>
            {
                PluginData? existing = _pluginDataDb.FindById(plugin.Info.Id);
                if (existing == null)
                {
                    _pluginDataDb.Insert(new PluginData
                    {
                        PluginId = plugin.Info.Id,
                        Data = data,
                    });
                }
                else
                {
                    existing.Data = data;
                    _pluginDataDb.Update(existing);
                }
            });
        }

        #endregion

        #region MESSAGES

        public IMessenger Messenger => App.GetService<IMessenger>();

        #endregion

        #region NOTIFICATION

        public void Info(InfoBarSeverity infoBarSeverity, string? title = null, string? msg = null, int? displayTimeMs = 3000)
            => _infoService.Info(infoBarSeverity, title, msg, displayTimeMs);

        public void Event(InfoBarSeverity infoBarSeverity, string title, Exception? exception = null, string? msg = null,
            Action? callbackAction = null, string? callbackButtonText = null) =>
            _infoService.Event(EventType.PluginEvent ,infoBarSeverity, title, exception, msg, callbackAction, callbackButtonText);

        public void DeveloperEvent(InfoBarSeverity infoBarSeverity = InfoBarSeverity.Warning, string? msg = null, Exception? e = null)
            => _infoService.DeveloperEvent(infoBarSeverity, msg, e);

        public void Log(InfoBarSeverity severity = InfoBarSeverity.Warning, string msg = "") =>
            _infoService.Log(severity, msg);

        #endregion

        #region BG_TASKS

        public Task AddBgTask(BgTaskBase bgTask) => _bgTaskService.AddBgTask(bgTask);

        public IEnumerable<BgTaskBase> GetBgTasks() => _bgTaskService.GetBgTasks();

        public T? GetBgTask<T>(string key) where T : BgTaskBase => _bgTaskService.GetBgTask<T>(key);

        public object? ActivationArgs => ActivationService.ActivationArgs;

        public LanguageEnum Language => _settingService.ReadSettingAsync<LanguageEnum>(KeyValues.Language).Result;

        #endregion

        #region SIDEBAR

        public void RegisterSidebarButton(SidebarButtonInfo button, Func<Task> onClick)
            => _sidebarService.RegisterPluginButton(plugin.Info.Id, plugin.Info.Name, button, onClick);

        public void UnregisterSidebarButton(string buttonId)
            => _sidebarService.UnregisterPluginButton(plugin.Info.Id, buttonId);

        #endregion

        #region UTILS

        public async Task<string?> DownloadImageAsync(string imageUrl, string imageName, HttpClient? client,
            Action<Exception>? onException = null)
        {
            StorageFolder imgFolder = await FileHelper.GetFolderAsync(FileHelper.FolderType.Images);
            DirectoryInfo pluginImgDir = new(Path.Combine(imgFolder.Path, plugin.Info.Id.ToString()));
            return await DownloadHelper.DownloadAndSaveImageWithDiffThread(imageUrl,
                fileNameWithoutExtension: imageName, onException: onException, client: client,
                targetFolder: pluginImgDir);
        }

        public string GetPluginPath()
        {
            if (plugin.Plugin is null) return plugin.Path;
            try
            {
                //对于热重载插件，其目录是一个临时目录
                return PluginXamlHost.GetRuntimePath(plugin.Plugin.GetType().Assembly);
            }
            catch
            {
                return plugin.Path;
            }
        }

        public void InvokeOnMainThread(Action action) => UiThreadInvokeHelper.Invoke(action);

        #endregion

        #region OBSOLETE_APIS

        /// <inheritdoc />
        [Obsolete($"请使用{nameof(AddGameInstallation)}")]
        public Task<Galgame> AddGame(string path, bool force = true, bool requireConfirm = true) =>
            AddGameInstallation(path, force, requireConfirm);

        #endregion
    }
}
