using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.Common;
using System.Security.Claims;
using AutoMapper.Internal;
using WebAPI.DAL.Entities;
using WebAPI.Models.Mutate;
using WebAPI.Models.View;

namespace WebAPI.Utilities;

public class AutomapperProfileConfiguration : AutoMapper.Profile
{
    public AutomapperProfileConfiguration()
        : this("WebApiProfile")
    {
    }

    protected AutomapperProfileConfiguration(string profileName)
        : base(profileName)
    {
        this.Internal().ForAllMaps((_,m) => m.MaxDepth(64));
        CreateMap<Address, AddressViewModel>();
        CreateMap<AddressMutateModel, Address>().MapOnlyIfChanged();
        CreateMap<Address, AddressMutateModel>().MapOnlyIfChanged();
    }
}