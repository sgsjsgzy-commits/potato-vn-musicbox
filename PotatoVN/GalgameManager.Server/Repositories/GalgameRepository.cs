using System;
using GalgameManager.Server.Contracts;
using GalgameManager.Server.Data;
using GalgameManager.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace GalgameManager.Server.Repositories;

public class GalgameRepository (DataContext context): IGalgameRepository
{
    private const int MaxRedirectDepth = 10; // 防止循环redirect导致无限循环

    public async Task<Galgame?> GetGalgameAsync(int id, bool includePlayTime = false)
    {
        IQueryable<Galgame> query = context.Galgame.AsQueryable();
        if(includePlayTime)
            query = query.Include(g => g.PlayTime);
        Galgame? result = await query.FirstOrDefaultAsync(g => g.Id == id);
        result = await FollowRedirectAsync(result, query);
        return result;
    }

    public async Task<Galgame?> GetGalgameCompleteAsync(int id)
    {
        IQueryable<Galgame> query = context.Galgame
            .Include(g => g.PlayTime)
            .Include(g => g.Characters)
            .Include(g => g.StaffGames)
            .AsSplitQuery();
        Galgame? result = await query.FirstOrDefaultAsync(g => g.Id == id);
        result = await FollowRedirectAsync(result, query);
        return result;
    }

    /// <summary>
    /// 跟随redirect链获取最终的游戏
    /// </summary>
    private async Task<Galgame?> FollowRedirectAsync(Galgame? galgame, IQueryable<Galgame> query)
    {
        HashSet<int> visited = [];
        Galgame? result = galgame;
        while (result is not null && result.RedirectTo != 0)
        {
            if (!visited.Add(result.Id))
                break; // 检测到循环，停止跟随
            if (visited.Count > MaxRedirectDepth)
                break; // 超过最大深度，停止跟随
            result = await query.FirstOrDefaultAsync(g => g.Id == result.RedirectTo);
        }
        return result;
    }

    public async Task<List<Galgame>> GetGalgamesAsync(List<int> ids, bool followRedirect = true)
    {
        List<Galgame> games = await context.Galgame.Where(g => ids.Contains(g.Id)).ToListAsync();
        if (!followRedirect) return games;

        List<Galgame> result = [];
        HashSet<int> addedIds = [];
        IQueryable<Galgame> query = context.Galgame.AsQueryable();
        foreach (Galgame game in games)
        {
            Galgame? finalGame = await FollowRedirectAsync(game, query);
            if (finalGame is not null && addedIds.Add(finalGame.Id))
                result.Add(finalGame);
        }
        return result;
    }

    public async Task<PagedResult<Galgame>> GetGalgamesAsync(int userId, long timestamp, int pageIndex, int pageSize,
        bool excludeRedirected = true)
    {
        if(pageIndex < 0 || pageSize < 0)
            throw new ArgumentException("Invalid page index or page size");

        IQueryable<Galgame> query = context.Galgame
            .Where(g => g.UserId == userId && g.LastChangedTimeStamp > timestamp);
        if (excludeRedirected) query = query.Where(g => g.RedirectTo == 0);
        var count = await query.CountAsync();
        List<Galgame> data = await query
            .AsSplitQuery()
            .Include(g => g.PlayTime)
            .Include(g => g.Characters)
            .Include(g => g.StaffGames)
            .OrderByDescending(g => g.Id)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync();
        return new PagedResult<Galgame>(data, pageIndex, pageSize, count);
    }

    public async Task<Galgame> AddGalgameAsync(Galgame galgame)
    {
        await context.Galgame.AddAsync(galgame);
        await context.SaveChangesAsync();
        return galgame;
    }

    public async Task AddOrUpdateGalgameAsync(Galgame galgame)
    {
        context.Galgame.Update(galgame);
        await context.SaveChangesAsync();
    }

    public async Task DeleteGalgameAsync(int id)
    {
        Galgame? galgame = await context.Galgame.FindAsync(id);
        if (galgame is not null)
        {
            context.Galgame.Remove(galgame);
            await context.SaveChangesAsync();
        }
    }

    public async Task<List<int>> DeleteGalgamesAsync(int userId)
    {
        IQueryable<Galgame> query = context.Galgame.Where(g => g.UserId == userId);
        List<int> ids = await query.Select(g => g.Id).ToListAsync();
        await query.ExecuteDeleteAsync();
        return ids;
    }

    public async Task<List<int>> GetRedirectChainAsync(int targetId)
    {
        // 获取所有直接或间接redirect到目标游戏的游戏ID
        // 由于redirect链可能有多层，需要递归查找
        List<int> result = [];
        HashSet<int> visited = [targetId];
        // 查找所有直接指向当前目标的游戏
        Queue<int> toProcess = new();
        toProcess.Enqueue(targetId);
        while (toProcess.Count > 0)
        {
            var currentTarget = toProcess.Dequeue();
            List<int> directRedirects = await context.Galgame
                .Where(g => g.RedirectTo == currentTarget)
                .Select(g => g.Id)
                .ToListAsync();
            foreach (var id in directRedirects.Where(id => visited.Add(id)))
            {
                result.Add(id);
                toProcess.Enqueue(id);
            }
        }
        return result;
    }

    public async Task<Galgame?> GetGalgameByUidAsync(int userId, string? bgmId, string? vndbId, string? name)
    {
        IQueryable<Galgame> query = context.Galgame
            .Where(g => g.UserId == userId && g.RedirectTo == 0); // 只搜索没有被redirect的游戏

        var hasBgmId = !string.IsNullOrEmpty(bgmId);
        var hasVndbId = !string.IsNullOrEmpty(vndbId);
        // 如果有任何ID，尝试通过ID匹配
        if (hasBgmId || hasVndbId)
        {
            List<Galgame> candidates = await query
                .Where(g => (hasBgmId && g.BgmId == bgmId) || (hasVndbId && g.VndbId == vndbId))
                .ToListAsync();
            foreach (Galgame candidate in candidates)
            {
                // 冲突：双方都有某个ID且不相同
                var bgmIdConflict = hasBgmId && !string.IsNullOrEmpty(candidate.BgmId) && candidate.BgmId != bgmId;
                var vndbIdConflict = hasVndbId && !string.IsNullOrEmpty(candidate.VndbId) && candidate.VndbId != vndbId;
                if (!bgmIdConflict && !vndbIdConflict)
                    return candidate;
            }
            return null;
        }

        // 只有在没有任何ID时，才使用基于name的匹配
        if (!string.IsNullOrEmpty(name))
        {
            Galgame? result = await query.FirstOrDefaultAsync(g => g.Name == name);
            if (result is not null)
                return result;
        }

        return null;
    }
}
