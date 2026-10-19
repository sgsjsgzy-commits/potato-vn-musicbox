namespace GalgameManager.Server.Enums;

/// <summary>
/// 也会作为某个staff与作品的关系
/// 作家：1
/// 画师：2
/// 音乐家：3
/// 声优：4
/// 制作人：5
/// 程序员：6
/// Unknown：114514
/// </summary>
public enum Career
{
    Unknown = 114514,
    Writer = 1,
    Painter = 2,
    Musician = 3,
    Seiyu = 4,
    Producer = 5,
    Programmer = 6,
}