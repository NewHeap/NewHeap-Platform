using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using WebAPI.DAL.Entities;
using WebAPI.Models.Mutate;

namespace WebAPI.Services
{
    public class AddressService
    {
        private readonly IStringLocalizer<AddressService> _localizer;
        private readonly IRepository<Address> _addressRepository;
        private readonly DbLogService _dbLogService;
        private readonly IMapper _mapper;
        private readonly LogHelperService _logHelper;
        protected readonly ValidationService _validationService;
        private readonly NhUserManager _userManager;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public AddressService(
            IRepository<Address> addressRepository,
            DbLogService dbLogService,
            LogHelperService logHelperService,
            IMapper mapper,
            IStringLocalizer<AddressService> localizer,
            ValidationService validationService,
            NhUserManager userManager,
            IHttpContextAccessor httpContextAccessor
            )
        {
            _addressRepository = addressRepository;
            _mapper = mapper;
            _dbLogService = dbLogService;
            _logHelper = logHelperService;
            _localizer = localizer;
            _validationService = validationService;
            _userManager = userManager;
            _httpContextAccessor = httpContextAccessor;
        }

        public IRepository<Address> GetRepository()
        {
            return _addressRepository;
        }

        #region Address
        private IQueryable<Address> QueryableWithAllIncludes(IQueryable<Address> queryable = null)
        {
            queryable ??= _addressRepository
                .GetAll()
            ;

            return queryable;
        }

        public async Task<Address> GetAsync(Guid id)
        {
            return await QueryableWithAllIncludes()
                .FirstOrDefaultAsync(m => m.Id == id);
        }

        public async Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<Address, Address, AddressMutateModel> model)
        {
            void sourceModelCheck()
            {
                if (model.SourceModel == null)
                {
                    model.TaskResult.AddError(string.Empty, _localizer["Action type requires a source model."]);
                }
            }

            async Task createUpdateCheck()
            {
                _validationService.ValidateMutateModelModelState(model);

                var counryCode = model.MutateModel.CountryCode?.Trim()?.ToLower();
                if ((counryCode?.Length ?? 0) != 2)
                {
                    model.TaskResult.AddError(nameof(model.MutateModel.CountryCode), _localizer["Invalid country code"]);
                }
            }

            if (model.ActionType == CRUDActionType.Create)
            {

                await createUpdateCheck();

            }
            else if (model.ActionType == CRUDActionType.Update)
            {
                sourceModelCheck();

                if (model.TaskResult.Success)
                {
                    await createUpdateCheck();
                }
            }
            else if (model.ActionType == CRUDActionType.Delete)
            {
                sourceModelCheck();
            }
            else
            {

            }
        }

        public async Task<TaskResult<Address>> CreateAsync(AddressMutateModel mutateModel, Guid? committedByUserId = null)
        {
            var result = new TaskResult<Address>();

            await ValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<Address, Address, AddressMutateModel>(CRUDActionType.Create)
            {
                TaskResult = result,
                SourceModel = null,
                MutateModel = mutateModel,
            });

            if (!result.Success)
            {
                return result;
            }

            var address = _mapper.Map<Address>(mutateModel);
            await _addressRepository.AddAsync(address);

            await _dbLogService.LogAsync(
                message: "Address create successful.",
                messageArguments: new string[] {
                    address.Id.ToString()
                },
                objectId: address.Id.ToString(),
                objectType: (typeof(Address)).Name,
                objectTypeFull: (typeof(Address)).FullName,
                userId: committedByUserId,
                action: LogAction.Create,
                type: LogType.Information,
                source: LogSource.Internal,
                tag: GetType().Name,
                doSaveChanges: false,
                dbContext: _addressRepository.Context
            );

            await _addressRepository.SaveChangesAsync();

            result.Data = address;

            return result;
        }

        public async Task<TaskResult<Address>> UpdateAsync(Guid id, AddressMutateModel mutateModel, Guid? committedByUserId = default)
        {
            var result = new TaskResult<Address>();

            var address = await _addressRepository
                .GetAll()
                .OrderBy(x => x.Id)
                .FirstOrDefaultAsync(x => x.Id == id);

            await ValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<Address, Address, AddressMutateModel>(CRUDActionType.Update)
            {
                TaskResult = result,
                SourceModel = address,
                MutateModel = mutateModel,
            });

            if (!result.Success)
            {
                return result;
            }

            var originalData = LogHelperService.Copy(address);

            address = _mapper.Map(mutateModel, address);
            address.LastModifiedDateTime = DateTimeOffset.UtcNow;

            var updatedData = LogHelperService.Copy(address);

            var changedProperties = await _logHelper.ChangedProperties(originalData, updatedData, new Dictionary<Expression<Func<Address, object>>, Func<object, Task<string>>>
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

            if (changedProperties.Any())
            {
                var values = string.Join("\n", changedProperties.Select(x => $"{x.Key}: '{x.OriginalValue}' -> '{x.UpdateValue}'"));
                await _dbLogService.LogAsync(
                    "Entity values updated",
                    messageArguments: new string[]
                    {
                        values
                    },
                    objectId: address.Id.ToString(),
                    objectType: typeof(Address).Name,
                    objectTypeFull: typeof(Address).FullName,
                    userId: committedByUserId,
                    action: LogAction.Update,
                    type: LogType.Information,
                    source: LogSource.Internal,
                    tag: GetType().Name,
                    doSaveChanges: false,
                    dbContext: _addressRepository.Context
                );
            }

            await _dbLogService.LogAsync(
                message: "Address update successful.",
                messageArguments: new string[] {
                    address.Id.ToString()
                },
                objectId: address.Id.ToString(),
                objectType: (typeof(Address)).Name,
                objectTypeFull: (typeof(Address)).FullName,
                userId: committedByUserId,
                action: LogAction.Update,
                type: LogType.Information,
                source: LogSource.Internal,
                tag: GetType().Name,
                doSaveChanges: false,
                dbContext: _addressRepository.Context
            );

            await _addressRepository.SaveChangesAsync();

            result.Data = address;

            return result;
        }

        public async Task<TaskResult<Address>> DeleteAsync(Guid id, Guid? committedByUserId = default)
        {
            var result = new TaskResult<Address>();

            var address = await _addressRepository
                .FindOneByAsync(x => x.Id == id);

            await ValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<Address, Address, AddressMutateModel>(CRUDActionType.Delete)
            {
                TaskResult = result,
                SourceModel = address,
                MutateModel = null,
            });

            if (!result.Success)
            {
                return result;
            }

            result.Data = address;
            _addressRepository.Remove(address);

            await _dbLogService.LogAsync(
                message: "Address remove successful.",
                messageArguments: new string[] {
                    address.Id.ToString()
                },
                objectId: address.Id.ToString(),
                objectType: (typeof(Address)).Name,
                objectTypeFull: (typeof(Address)).FullName,
                userId: committedByUserId,
                action: LogAction.Delete,
                type: LogType.Information,
                source: LogSource.Internal,
                tag: GetType().Name,
                doSaveChanges: false,
                dbContext: _addressRepository.Context
            );

            await _addressRepository.SaveChangesAsync();

            return result;
        }
        #endregion
    }
}
