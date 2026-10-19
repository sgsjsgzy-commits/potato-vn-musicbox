using System.ComponentModel.DataAnnotations;
using AutoMapper;
using GalgameManager.Server.Contracts;
using GalgameManager.Server.Enums;

namespace GalgameManager.Server.Models;

public class StaffBase
{
    public int Id { get; set; }
    public bool IsDeleted { get; set; }
    [MaxLength(200)] public string? BgmId { get; set; }
    [MaxLength(200)] public string? VndbId { get; set; }
    [MaxLength(200)] public string? YmgalId { get; set; }
    [MaxLength(200)] public string? JapaneseName { get; set; }
    [MaxLength(200)] public string? EnglishName { get; set; }
    [MaxLength(200)] public string? ChineseName { get; set; }
    public Gender Gender { get; set; }
    [MaxLength(10000)] public string? Description { get; set; }
    public long BirthDateTimestamp { get; set; }
    [MaxLength(2000)] public string? ExternalImageLink { get; set; }
}

public class Staff : StaffBase
{
    public int UserId { get; set; }
    public User? User { get; set; }
    [MaxLength(1000)] public string? ImageLoc { get; set; }
    public List<StaffGame> StaffGames { get; set; } = [];
    public long LastModifyTimestamp { get; set; }
}

public class StaffUpdateDto : StaffBase
{
    public new int? Id { get; set; }
    public new bool? IsDeleted { get; set; }
    public string? ImageLoc { get; set; }
    public new Gender? Gender { get; set; }
    public new long? BirthDateTimestamp { get; set; }
    public List<StaffGameUpdateDto>? StaffGames { get; set; }
}

public class StaffDto : StaffBase
{
    public string? ImageUrl { get; set; }
    public long LastModifyTimestamp { get; set; }
    public List<StaffGameDto> StaffGames { get; set; } = [];
}

public class StaffGame
{
    public int StaffId { get; set; }
    public required Staff Staff { get; set; }
    public int GameId { get; set; }
    public required Galgame Game { get; set; }

    public List<Career> Relation { get; set; } = [];
}

public class StaffGameDto
{
    public int StaffId { get; set; }
    public int GameId { get; set; }
    public List<Career> Relation { get; set; } = [];
}

public class StaffGameUpdateDto
{
    public int GameId { get; set; }
    public List<Career> Relation { get; set; } = [];
}

public class StaffProfile : Profile
{
    public StaffProfile()
    {
        CreateMap<StaffGameDto, StaffGame>();
        CreateMap<StaffGame, StaffGameDto>();

        CreateMap<Staff, StaffDto>();

        CreateMap<StaffGameUpdateDto, StaffGame>();
    }
}