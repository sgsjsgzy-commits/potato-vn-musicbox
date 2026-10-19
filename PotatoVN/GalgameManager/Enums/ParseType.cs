namespace GalgameManager.Enums;

[Flags]
public enum GameParseType
{
    None = 0,
    HeaderImage = 1 << 0,
    Character = 1 << 1,
    /// 游玩状态（评论、评分等）
    PlayStatus = 1 << 2,
    /// 游戏封面图
    Image = 1 << 3,
    /// 游戏信息（如游戏名、发行日期、简介、Tag等）
    GameInfo = 1 << 4,
    All = int.MaxValue, 
}
