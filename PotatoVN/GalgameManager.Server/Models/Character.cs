using System.ComponentModel.DataAnnotations;
using GalgameManager.Server.Enums;

namespace GalgameManager.Server.Models;

public class CharacterBase
{
    public int Id { get; set; }
    /// 角色名
    [MaxLength(200)] public string? Name { get; set; }
    /// 角色与游戏的关系（如主角、配角等）
    [MaxLength(200)] public string? Relation { get; set; }
    /// 角色简介
    [MaxLength(10000)] public string? Summary { get; set; }
    /// 性别
    public Gender Gender { get; set; } = Gender.Unknown;
    /// 出生年
    public int BirthYear { get; set; }
    /// 出生月
    public int BirthMonth { get; set; }
    /// 出生日
    public int BirthDay { get; set; }
    /// 血型
    [MaxLength(50)] public string? BloodType { get; set; }
    /// 身高
    [MaxLength(50)] public string? Height { get; set; }
    /// 体重
    [MaxLength(50)] public string? Weight { get; set; }
    /// 三围
    [MaxLength(50)] public string? ThreeSize { get; set; }
}

public class Character : CharacterBase
{
    public Galgame? Galgame { get; set; }
    public int GalgameId { get; set; }
    [MaxLength(220)] public string? PreviewImageLoc { get; set; }
    [MaxLength(220)] public string? ImageLoc { get; set; }
}