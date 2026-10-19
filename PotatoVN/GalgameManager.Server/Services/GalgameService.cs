using AutoMapper;
using GalgameManager.Core.Helpers;
using GalgameManager.Server.Contracts;
using GalgameManager.Server.Models;

namespace GalgameManager.Server.Services;

public class GalgameService(IGalgameRepository galRep, IGalgameDeletedRepository galDeletedRep, 
    IPlayLogRepository playLogRep, ICharacterRepository characterRep, IUserService userService, IOssService ossService,
    IMapper mapper)
    : IGalgameService
{
    public async Task<Galgame> GetGalgameAsync(int userId, int id, bool complete = false)
    {
        Galgame? galgame = complete ? await galRep.GetGalgameCompleteAsync(id) : await galRep.GetGalgameAsync(id);
        if (galgame == null) throw new ArgumentException("Galgame not found.");
        if (galgame.UserId != userId) throw new UnauthorizedAccessException("You are not the owner of this galgame.");
        return galgame;
    }

    public async Task<PagedResult<Galgame>> GetGalgamesAsync(int userId, long timestamp, int pageIndex, int pageSize)
    {
        return await galRep.GetGalgamesAsync(userId, timestamp, pageIndex, pageSize);
    }

    public async Task<Galgame> AddOrUpdateGalgameAsync(int userId, GalgameUpdateDto payload)
    {
        Galgame? galgame;
        if (payload.Id != null)
        {
            galgame = await galRep.GetGalgameAsync(payload.Id ?? 0);
            if (galgame is null)
                throw new ArgumentException("Galgame not found.");
            if(galgame.UserId != userId)
                throw new UnauthorizedAccessException("You are not the owner of this galgame.");
        }
        else
        {
            // 当 Id 为空时，先检查该用户是否已经有这个游戏
            galgame = await galRep.GetGalgameByUidAsync(userId, payload.BgmId, payload.VndbId, payload.Name);
            if (galgame is null) // 该用户没有这个游戏，新分配一个游戏id
            {
                galgame = new Galgame
                {
                    UserId = userId,
                };
                await galRep.AddGalgameAsync(galgame);
            }
        }

        galgame.BgmId = payload.BgmId ?? galgame.BgmId;
        galgame.VndbId = payload.VndbId ?? galgame.VndbId;
        galgame.Name = payload.Name ?? galgame.Name;
        galgame.CnName = payload.CnName ?? galgame.CnName;
        galgame.Description = payload.Description ?? galgame.Description;
        galgame.Developer = payload.Developer ?? galgame.Developer;
        galgame.ExpectedPlayTime = payload.ExpectedPlayTime ?? galgame.ExpectedPlayTime;
        if (payload.Rating is not null && !float.IsNaN(payload.Rating.Value) && !float.IsInfinity(payload.Rating.Value))
            galgame.Rating = payload.Rating.Value;
        galgame.ReleaseDateTimeStamp = payload.ReleaseDateTimeStamp ?? galgame.ReleaseDateTimeStamp;
        if (!string.IsNullOrEmpty(payload.ImageLoc) && payload.ImageLoc != galgame.ImageLoc) 
            await ossService.DeleteObjectAsync(userId, galgame.ImageLoc);
        galgame.ImageLoc = payload.ImageLoc ?? galgame.ImageLoc;
        if (!string.IsNullOrEmpty(payload.HeaderImageOssLoc) && payload.HeaderImageOssLoc != galgame.HeaderImageOssPosition)
            await ossService.DeleteObjectAsync(userId, galgame.HeaderImageOssPosition);
        galgame.HeaderImageOssPosition = payload.HeaderImageOssLoc ?? galgame.HeaderImageOssPosition;
        galgame.HeaderImageUrl = payload.HeaderImageExternalUrl ?? galgame.HeaderImageUrl;
        galgame.Tags = payload.Tags ?? galgame.Tags;
        
        if (payload.PlayTime is not null)
        {
            List<PlayLog> newLogs = [];
            foreach (PlayLogDto playLogDto in payload.PlayTime)
                newLogs.Add(new PlayLog
                {
                    GalgameId = galgame.Id,
                    DateTimeStamp = playLogDto.DateTimeStamp,
                    Minute = playLogDto.Minute
                });
            await playLogRep.SetPlayLogsAsync(galgame.Id, newLogs);
        }

        galgame.TotalPlayTime = payload.TotalPlayTime ?? galgame.TotalPlayTime;
        galgame.PlayType = payload.PlayType ?? galgame.PlayType;
        galgame.Comment = payload.Comment ?? galgame.Comment;
        galgame.MyRate = payload.MyRate ?? galgame.MyRate;
        galgame.PrivateComment = payload.PrivateComment ?? galgame.PrivateComment;
        galgame.PlayCount = payload.PlayCount ?? galgame.PlayCount;

        if (payload.Characters is not null)
        {
            List<Character> characters = [];
            foreach (CharacterUpdateDto dto in payload.Characters)
            {
                Character? tmp = mapper.Map<Character>(dto);
                if (tmp is null) continue;
                tmp.GalgameId = galgame.Id;
                characters.Add(tmp);
            }
            await characterRep.UpdateCharacterAsync(userId, galgame.Id, characters);
            galgame.CharacterLastChangedTimeStamp = DateTime.Now.ToUnixTime();
        }
        
        galgame.LastChangedTimeStamp = DateTime.Now.ToUnixTime();
        await galRep.AddOrUpdateGalgameAsync(galgame);
        await userService.UpdateLastModifiedAsync(userId, galgame.LastChangedTimeStamp);

        return galgame;
    }

    public async Task<Galgame?> AddPlayLogAsync(int userId, int galgameId, PlayLogDto payload)
    {
        Galgame? galgame = await galRep.GetGalgameAsync(galgameId, true);
        if (galgame is null)
            throw new ArgumentException("Galgame not found.");
        if (galgame.UserId != userId)
            throw new UnauthorizedAccessException("You are not the owner of this galgame.");
        var actualGameId = galgame.Id;
        PlayLog? log = await playLogRep.GetPlayLogAsync(actualGameId, payload.DateTimeStamp);
        if (log is not null)
            log.Minute += payload.Minute;
        else
        {
            log = new()
            {
                GalgameId = actualGameId,
                DateTimeStamp = payload.DateTimeStamp,
                Minute = payload.Minute
            };
        }

        await playLogRep.AddOrUpdatePlayLogAsync(log);
        galgame.TotalPlayTime += payload.Minute;
        galgame.LastChangedTimeStamp = DateTime.Now.ToUnixTime();
        await galRep.AddOrUpdateGalgameAsync(galgame);
        await userService.UpdateLastModifiedAsync(userId, galgame.LastChangedTimeStamp);
        
        return galgame;
    }
    
    public async Task DeleteGalgameAsync(int userId, int id)
    {
        Galgame? gal = await galRep.GetGalgameAsync(id);
        if (gal is null) throw new ArgumentException("Galgame not found.");
        if (gal.UserId != userId) throw new UnauthorizedAccessException("You are not the owner of this galgame.");
        var actualGameId = gal.Id; //最后指向游戏的id
        var timestamp = DateTime.Now.ToUnixTime();
        // 删除 redirect 链上所有的游戏
        List<int> redirectChain = await galRep.GetRedirectChainAsync(actualGameId);
        List<Galgame> chainGames = await galRep.GetGalgamesAsync(redirectChain, followRedirect: false);
        foreach (Galgame chainGame in chainGames)
        {
            if (chainGame.ImageLoc is not null) await ossService.DeleteObjectAsync(userId, chainGame.ImageLoc);
            if (chainGame.HeaderImageOssPosition is not null) await ossService.DeleteObjectAsync(userId, chainGame.HeaderImageOssPosition);
        }
        foreach (var chainId in redirectChain)
        {
            await galRep.DeleteGalgameAsync(chainId);
            await galDeletedRep.AddGalgameDeletedAsync(new GalgameDeleted
            {
                DeleteTimeStamp = timestamp,
                GalgameId = chainId,
                UserId = userId
            });
        }
        
        // 删除目标游戏本身
        await galRep.DeleteGalgameAsync(actualGameId);
        await galDeletedRep.AddGalgameDeletedAsync(new GalgameDeleted
        {
            DeleteTimeStamp = timestamp,
            GalgameId = actualGameId,
            UserId = gal.UserId
        });
        await userService.UpdateLastModifiedAsync(gal.UserId, timestamp);
        if(gal.ImageLoc is not null) await ossService.DeleteObjectAsync(userId, gal.ImageLoc);
        if(gal.HeaderImageOssPosition is not null) await ossService.DeleteObjectAsync(userId, gal.HeaderImageOssPosition);
    }

    public async Task DeleteGalgamesAsync(int userId)
    {
        var timestamp = DateTime.Now.ToUnixTime();
        PagedResult<Galgame> gals = await galRep.GetGalgamesAsync(userId, 0, 0, 1000000, excludeRedirected: false);
        foreach (Galgame game in gals.Items)
        {
            if(game.ImageLoc is not null) await ossService.DeleteObjectAsync(userId, game.ImageLoc);
            if(game.HeaderImageOssPosition is not null) await ossService.DeleteObjectAsync(userId, game.HeaderImageOssPosition);
        }
        List<int> ids = await galRep.DeleteGalgamesAsync(userId);
        foreach (var id in ids)
            await galDeletedRep.AddGalgameDeletedAsync(new GalgameDeleted
            {
                DeleteTimeStamp = timestamp,
                GalgameId = id,
                UserId = userId
            });
        await userService.UpdateLastModifiedAsync(userId, timestamp);
    }

    public async Task<PagedResult<GalgameDeleted>> GetDeletedGalgamesAsync(int userId, long timestamp, int pageIndex,
        int pageSize)
    {
        if(pageIndex < 0 || pageSize < 0)
            throw new ArgumentException("Invalid pageIndex or pageSize.");
        return await galDeletedRep.GetGalgameDeletedsAsync(userId, timestamp, pageIndex, pageSize);
    }
}
