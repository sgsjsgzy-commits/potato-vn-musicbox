using System.ComponentModel.DataAnnotations;
using GalgameManager.Core.Helpers;
using GalgameManager.Server.Enums;
// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace GalgameManager.Server.Models;

public class Galgame
{
    public const string DefaultString = "——";


    public int Id { get; set; }
    public int RedirectTo { get; set; } // 因为旧版本设计缺陷，部分服务端游戏重复了，这个redirectTo用来把重复的游戏导向唯一正确游戏
    public User? User { get; set; }
    public required int UserId { get; set; }

    public long LastChangedTimeStamp { get; set; }
    public long CharacterLastChangedTimeStamp { get; set; }
    
    #region GAME_SETTINGS
    public List<Category>? Categories { get; set; } = new();
    #endregion
    
    #region GAME_INFO
    [MaxLength(20)] public string? BgmId { get; set; }
    [MaxLength(20)] public string? VndbId { get; set; }
    [MaxLength(200)] public string Name { get; set; } = string.Empty;
    [MaxLength(200)] public string CnName { get; set; } = string.Empty;
    [MaxLength(25000)] public string Description { get; set; } = string.Empty;
    [MaxLength(200)] public string Developer { get; set; } = DefaultString;
    [MaxLength(200)] public string ExpectedPlayTime { get; set; } = DefaultString;
    public float Rating { get; set; }
    public long ReleaseDateTimeStamp { get; set; }
    [MaxLength(220)] public string? ImageLoc { get; set; }
    [MaxLength(500)] public string? HeaderImageUrl { get; set; }
    [MaxLength(220)] public string? HeaderImageOssPosition { get; set; }
    public List<string>? Tags { get; set; }
    public List<Character> Characters { get; set; } = [];
    public List<StaffGame> StaffGames { get; set; } = [];

    #endregion

    #region PLAY_STATUS
    public List<PlayLog>? PlayTime { get; set; } = new();
    public int TotalPlayTime { get; set; } //单位：分钟
    public int PlayCount { get; set; } //游玩次数
    public PlayType PlayType { get; set; } //游玩状态
    [MaxLength(1000)] public string Comment { get; set; } = string.Empty; //吐槽（评论）
    public int MyRate { get; set; } //我的评分
    public bool PrivateComment { get; set; } //是否私密评论
    #endregion
}
