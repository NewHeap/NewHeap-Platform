using AutoMapper;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Services;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading.Tasks;
using WebAPI.DAL.Entities;
using WebAPI.Models.Mutate;
using WebAPI.Models.View;

namespace WebAPI.Services
{
    public class AddressService : BaseDbEntityService<Address, AddressMutateModel, AddressViewModel, AddressService>
    {
        public AddressService(
            IRepository<Address> repository, 
            DbLogService dbLogService, 
            LogHelperService logHelperService, 
            IMapper mapper, 
            IStringLocalizer<AddressService> localizer, 
            ValidationService validationService, 
            NhUserManager userManager) 
            : base(repository, dbLogService, logHelperService, mapper, localizer, validationService, userManager)
        {
        }

        protected override Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperies(Address original, Address updated)
        {
            return _logHelper.ChangedProperties(original, updated, new Dictionary<Expression<Func<Address, object>>, Func<object, Task<string>>>
            {
                // Method resolvers
            },
                x => x.Country,
                x => x.CountryCode,
                x => x.Province,
                x => x.Municipality,
                x => x.Place,
                x => x.PostalCode,
                x => x.Street,
                x => x.StreetObjectNumber,
                x => x.StreetObjectNumberSuffix,
                x => x.StreetObjectRoomNumber,
                x => x.LocationDescription,
                x => x.LocationLongitude,
                x => x.LocationLatitude
            );
        }
    }
}
