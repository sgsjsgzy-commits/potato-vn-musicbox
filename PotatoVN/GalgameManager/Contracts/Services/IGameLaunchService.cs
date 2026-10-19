using GalgameManager.Models;
using GalgameManager.Models.Sources;

namespace GalgameManager.Contracts.Services;

/// <summary>
/// 负责按明确安装实例启动游戏并衔接游玩期间后台任务。
/// </summary>
public interface IGameLaunchService
{
    /// <summary>
    /// 启动指定游戏的指定安装实例。
    /// </summary>
    /// <param name="game">逻辑游戏</param>
    /// <param name="installation">要启动的安装实例</param>
    Task LaunchAsync(Galgame game, GalgameAndPath installation);
}
