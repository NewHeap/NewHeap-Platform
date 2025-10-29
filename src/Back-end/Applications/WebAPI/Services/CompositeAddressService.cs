using AutoMapper;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using WebAPI.DAL.Entities;
using WebAPI.Models.Mutate;
using WebAPI.Models.View;

namespace WebAPI.Services
{
    public class CompositeAddressService : CompositeBaseDbEntityService<Address, AddressMutateModel, Address, CompositeAddressService>
    {
        public CompositeAddressService(
            IRepository<Address> repository, 
            NhDbLogService dbLogService, 
            LogHelperService logHelperService, 
            IMapper mapper, 
            IStringLocalizer<CompositeAddressService> localizer, 
            ValidationService validationService
            
            )
              : base(repository, dbLogService, logHelperService, mapper, localizer, validationService)
        {
        }

        protected override Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<Address, Address, AddressMutateModel> model, CancellationToken cancellationToken = default)
        {
            return DoValidateCreateUpdateDeleteAsync(model, cancellationToken);
        }

        public TaskResult TestLocalization()
        {
            var result = new TaskResult();

            result.AddError("test", _localizer["Ëmm {0} testteenn {1}", "bla", "bla"]);
            return result;
        }

        public override Task<TaskResult<Address>> CreateAsync(AddressMutateModel mutateModel, Guid? committedByUserId = null, Action<Address> beforeSave = null, CancellationToken cancellationToken = default, CompositeBaseDbEntityServiceOperationOptions options = null)
        {
            return DoCreateAsync(
                mutateModel,
                committedByUserId,
                (Address x) =>
                {
                    beforeSave?.Invoke(x);
                },
                cancellationToken:
                cancellationToken,
                options: options
            );
        }

        public override Task<TaskResult<Address>> DeleteAsync(Guid id, Guid? committedByUserId = null, CancellationToken cancellationToken = default, CompositeBaseDbEntityServiceOperationOptions options = null)
        {
            throw new NotImplementedException();
        }

        public override Task<Address> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public override Task<TaskResult<Address>> UpdateAsync(Guid id, AddressMutateModel mutateModel, Guid? committedByUserId = null, Action<Address> beforeSave = null, CancellationToken cancellationToken = default, CompositeBaseDbEntityServiceOperationOptions options = null)
        {
            throw new NotImplementedException();
        }
    }
}
