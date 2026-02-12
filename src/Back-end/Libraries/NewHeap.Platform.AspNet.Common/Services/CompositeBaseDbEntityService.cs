using AutoMapper;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Utilities;

namespace NewHeap.Platform.AspNet.Common.Services;

public interface ICompositeBaseDbEntityServiceOperationOptions : IAbstractBaseDbEntityServiceOperationOptions
{ }

public class CompositeBaseDbEntityServiceOperationOptions : AbstractBaseDbEntityServiceOperationOptions, ICompositeBaseDbEntityServiceOperationOptions
{ }

public interface ICompositeBaseDbEntityService<TEntity, TMutateModel, TViewModel> : ICompositeBaseDbEntityService<TEntity, TMutateModel, TMutateModel, TMutateModel, TViewModel>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
{
}

public interface ICompositeBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TViewModel> : IAbstractBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, CompositeBaseDbEntityServiceOperationOptions>
    where TEntity : class, IdDbEntity
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
{
    Task<TaskResult<TViewModel?>> CreateAsync(TCreateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default, CompositeBaseDbEntityServiceOperationOptions? options = null);
    Task<TaskResult<TViewModel?>> DeleteAsync(Guid id, Guid? committedByUserId = null, CancellationToken cancellationToken = default, CompositeBaseDbEntityServiceOperationOptions? options = null);
    Task<TViewModel?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TaskResult<TViewModel?>> UpdateAsync(Guid id, TUpdateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default, CompositeBaseDbEntityServiceOperationOptions? options = null);
    Task<TaskResult<TEntity?>> UpdatePartialAsync(
        Guid id,
        Func<NhSetPropertyCalls<TUpdateMutateModel>, NhSetPropertyCalls<TUpdateMutateModel>> set,
        Action<NhSetPropertyCalls<TUpdateMutateModel>>? callsReady = null,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null,
        CancellationToken cancellationToken = default,
        CompositeBaseDbEntityServiceOperationOptions? options = null
    );
}

public abstract partial class CompositeBaseDbEntityService<TEntity, TMutateModel, TViewModel, TCompositeBaseDbEntityService> : CompositeBaseDbEntityService<TEntity, TMutateModel, TMutateModel, TMutateModel, TViewModel, TCompositeBaseDbEntityService>, ICompositeBaseDbEntityService<TEntity, TMutateModel, TViewModel>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
    where TCompositeBaseDbEntityService : CompositeBaseDbEntityService<TEntity, TMutateModel, TViewModel, TCompositeBaseDbEntityService>
{
    protected CompositeBaseDbEntityService(IRepository<TEntity> repository, INhDbLogService dbLogService, LogHelperService logHelperService, IMapper mapper, IStringLocalizer<TCompositeBaseDbEntityService> localizer, ValidationService validationService) : base(repository, dbLogService, logHelperService, mapper, localizer, validationService)
    {
    }

    protected abstract Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default);

    protected virtual async Task DoValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default)
    {
        void sourceModelCheck()
        {
            if (model.SourceModel == null)
            {
                model.TaskResult.AddError(string.Empty, _localizer["Action type requires a source model."]);
            }
        }

        Task createUpdateCheck()
        {
            _validationService.ValidateMutateModelModelState(model);
            return Task.CompletedTask;
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

    protected sealed override Task DoValidateCreateAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default)
    {
        return ValidateCreateUpdateDeleteAsync(model, cancellationToken);
    }

    protected sealed override Task DoValidateUpdateAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default)
    {
        return ValidateCreateUpdateDeleteAsync(model, cancellationToken);
    }

    protected sealed override Task DoValidateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default)
    {
        return ValidateCreateUpdateDeleteAsync(model, cancellationToken);
    }
}

public abstract partial class CompositeBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TViewModel, TCompositeBaseDbEntityService> : AbstractBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TCompositeBaseDbEntityService, ICompositeBaseDbEntityServiceOperationOptions>, ICompositeBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TViewModel> 
    where TEntity : class, IdDbEntity
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
    where TCompositeBaseDbEntityService : CompositeBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TViewModel, TCompositeBaseDbEntityService>
{
    protected CompositeBaseDbEntityService(
        IRepository<TEntity> repository,
        INhDbLogService dbLogService, 
        LogHelperService logHelperService, 
        IMapper mapper, 
        IStringLocalizer<TCompositeBaseDbEntityService> localizer, 
        ValidationService validationService
        ) : base(repository, dbLogService, logHelperService, mapper, localizer, validationService)
    {
    }

    #region TEntity

    public abstract Task<TViewModel?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    public abstract Task<TaskResult<TViewModel?>> CreateAsync(TCreateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default, CompositeBaseDbEntityServiceOperationOptions? options = null);

    public abstract Task<TaskResult<TViewModel?>> UpdateAsync(
        Guid id,
        TUpdateMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null,
        CancellationToken cancellationToken = default,
        CompositeBaseDbEntityServiceOperationOptions? options = null
        );

    public virtual Task<TaskResult<TEntity?>> UpdatePartialAsync(
        Guid id,
        Func<NhSetPropertyCalls<TUpdateMutateModel>, NhSetPropertyCalls<TUpdateMutateModel>> set,
        Action<NhSetPropertyCalls<TUpdateMutateModel>>? callsReady = null,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null,
        CancellationToken cancellationToken = default,
        CompositeBaseDbEntityServiceOperationOptions? options = null
    )
    { 
        return DoUpdatePartialAsync(id, set, callsReady, committedByUserId, beforeSave, cancellationToken, options);
    }

    public abstract Task<TaskResult<TViewModel?>> DeleteAsync(Guid id, Guid? committedByUserId = default, CancellationToken cancellationToken = default, CompositeBaseDbEntityServiceOperationOptions? options = null);
    #endregion
}
