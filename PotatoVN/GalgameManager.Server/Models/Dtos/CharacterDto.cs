using AutoMapper;
using GalgameManager.Server.Contracts;

namespace GalgameManager.Server.Models;

public class CharacterDto : CharacterBase
{
    public string? ImageUrl { get; set; }
    public string? PreviewImageUrl { get; set; }
    
    public async Task<CharacterDto> WithImgAsync(Character character, IOssService ossService, int userId)
    {
        ImageUrl = await ossService.GetReadPresignedUrlAsync(userId, character.ImageLoc);
        PreviewImageUrl = await ossService.GetReadPresignedUrlAsync(userId, character.PreviewImageLoc);
        return this;
    }
}

public class CharacterUpdateDto : CharacterBase
{
    public string? ImageLoc { get; set; }
    public string? PreviewImageLoc { get; set; }
}

public class CharacterProfile : Profile
{
    public CharacterProfile()
    {
        CreateMap<CharacterUpdateDto, Character>();
        CreateMap<Character, CharacterDto>();
    }
}