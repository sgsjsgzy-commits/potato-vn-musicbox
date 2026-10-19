using System.Runtime.CompilerServices;
using AutoMapper;
using PotatoVN.Client.Model;

[assembly: TypeForwardedTo(typeof(GalgameManager.Models.Staff))]
[assembly: TypeForwardedTo(typeof(GalgameManager.Models.StaffRelation))]
[assembly: TypeForwardedTo(typeof(GalgameManager.Models.StaffGame))]
[assembly: TypeForwardedTo(typeof(GalgameManager.Models.StaffIdentifier))]

namespace GalgameManager.Models.MapperProfile;

public class StaffMapper : Profile
{
    public StaffMapper()
    {
        CreateMap<StaffDto, Staff>();
    }
}