using GalgameManager.Server.Models;

namespace GalgameManager.Server.Contracts;

public interface IGalgameRepository
{
    public Task<Galgame?> GetGalgameAsync(int id, bool includePlayTime = false);

    public Task<Galgame?> GetGalgameCompleteAsync(int id);
    
    /// <summary>
    /// 获取一系列galgame，找不到游戏不返回在列表中
    /// </summary>
    /// <param name="ids">游戏ID列表</param>
    /// <param name="followRedirect">是否跟随重定向链返回最终的游戏，默认为 true</param>
    /// <returns></returns>
    public Task<List<Galgame>> GetGalgamesAsync(List<int> ids, bool followRedirect = true);
    
    /// <summary>
    /// 获取指定用户的最后一次更新时间在指定时间戳之后（严格大于）的Galgame列表
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="timestamp">时间戳</param>
    /// <param name="pageIndex">页码</param>
    /// <param name="pageSize">每页大小</param>
    /// <param name="excludeRedirected">是否排除有redirect的游戏（即只返回链上最后的游戏）</param>
    public Task<PagedResult<Galgame>> GetGalgamesAsync(int userId, long timestamp, int pageIndex, int pageSize,
        bool excludeRedirected = true);
    
    public Task<Galgame> AddGalgameAsync(Galgame galgame);
    
    public Task AddOrUpdateGalgameAsync(Galgame galgame);
    
    public Task DeleteGalgameAsync(int id);
    
    public Task<List<int>> DeleteGalgamesAsync(int userId);
    
    /// <summary>
    /// 获取指向目标游戏的redirect链上所有游戏ID（不包括目标游戏本身）
    /// </summary>
    /// <param name="targetId">目标游戏ID</param>
    /// <returns>所有redirect到目标游戏的游戏ID表</returns>
    public Task<List<int>> GetRedirectChainAsync(int targetId);

    /// <summary>
    /// 根据游戏唯一标识（BgmId、VndbId或Name）查找用户的游戏
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="bgmId">Bangumi ID</param>
    /// <param name="vndbId">VNDB ID</param>
    /// <param name="name">游戏名称</param>
    /// <returns>找到的游戏，如果不存在返回null</returns>
    public Task<Galgame?> GetGalgameByUidAsync(int userId, string? bgmId, string? vndbId, string? name);
}