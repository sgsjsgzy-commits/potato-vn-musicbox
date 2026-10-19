using AutoMapper;
using GalgameManager.Enums;
using PotatoVN.Client.Model;

namespace GalgameManager.Models.MapperProfile;

public class CharacterMapper : Profile
{
    public CharacterMapper()
    {
        CreateMap<GalgameCharacter, CharacterUpdateDto>()
            .ForMember(dto => dto.ThreeSize, exp => exp.MapFrom(character => character.BWH))
            .ForMember(dto => dto.Id, exp => exp.MapFrom(character => character.Ids[(int)RssType.PotatoVn]));

        CreateMap<CharacterDto, GalgameCharacter>()
            .ForMember(character => character.BWH, exp => exp.MapFrom(dto => dto.ThreeSize));
    }
}