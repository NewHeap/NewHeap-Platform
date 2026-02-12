using AutoMapper;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using NewHeap.Platform.Common.Utilities;

namespace NewHeap.Platform.AspNet.Common.Services;


public interface IBaseDbEntityServiceOperationOptions : IAbstractBaseDbEntityServiceOperationOptions
{ }

public class BaseDbEntityServiceOperationOptions : AbstractBaseDbEntityServiceOperationOptions, IBaseDbEntityServiceOperationOptions
{ }

public interface IBaseDbEntityService<TEntity, TMutateModel> : IBaseDbEntityService<TEntity, TMutateModel, TMutateModel, TMutateModel>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
{ 

}

public interface IBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel> : IAbstractBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, BaseDbEntityServiceOperationOptions>
    where TEntity : class, IdDbEntity
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
{
    Task<TaskResult<TEntity?>> CreateAsync(TCreateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default, BaseDbEntityServiceOperationOptions? options = null);
    Task<TaskResult<TEntity?>> DeleteAsync(Guid id, Guid? committedByUserId = null, CancellationToken cancellationToken = default, BaseDbEntityServiceOperationOptions? options = null);
    Task<TEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TaskResult<TEntity?>> UpdateAsync(Guid id, TUpdateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default, BaseDbEntityServiceOperationOptions? options = null);

    Task<TaskResult<TEntity?>> UpdatePartialAsync(
        Guid id,
        Func<NhSetPropertyCalls<TUpdateMutateModel>, NhSetPropertyCalls<TUpdateMutateModel>> set,
        Action<NhSetPropertyCalls<TUpdateMutateModel>>? callsReady = null,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null
    );
}

public abstract partial class BaseDbEntityService<TEntity, TMutateModel, TBaseDbEntityService> : BaseDbEntityService<TEntity, TMutateModel, TMutateModel, TMutateModel, TBaseDbEntityService>, IBaseDbEntityService<TEntity, TMutateModel>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
    where TBaseDbEntityService : BaseDbEntityService<TEntity, TMutateModel, TBaseDbEntityService>
{
    protected BaseDbEntityService(
        IRepository<TEntity> repository, 
        INhDbLogService dbLogService, 
        LogHelperService logHelperService, 
        IMapper mapper, 
        IStringLocalizer<TBaseDbEntityService> localizer, 
        ValidationService validationService) : base(repository, dbLogService, logHelperService, mapper, localizer, validationService)
    {
    }

    protected virtual Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default)
        => DoValidateCreateUpdateDeleteAsync(model, cancellationToken);

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

public abstract partial class BaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TBaseDbEntityService> : AbstractBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TBaseDbEntityService, IBaseDbEntityServiceOperationOptions>, IBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel> 
    where TEntity : class, IdDbEntity
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
    where TBaseDbEntityService : BaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TBaseDbEntityService>
{
    protected BaseDbEntityService(
        IRepository<TEntity> repository,
        INhDbLogService dbLogService, 
        LogHelperService logHelperService, 
        IMapper mapper, 
        IStringLocalizer<TBaseDbEntityService> localizer, 
        ValidationService validationService
        ) : base(repository, dbLogService, logHelperService, mapper, localizer, validationService)
    {
    }

    #region TEntity

    public virtual Task<TEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default) 
        => DoGetAsync(id, cancellationToken);

    public virtual Task<TaskResult<TEntity?>> CreateAsync(TCreateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default, BaseDbEntityServiceOperationOptions? options = null)
        => DoCreateAsync(mutateModel, committedByUserId, beforeSave, cancellationToken, options);

    public virtual Task<TaskResult<TEntity?>> UpdateAsync(
        Guid id,
        TUpdateMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null
        )
        => DoUpdateAsync(id, mutateModel, committedByUserId, beforeSave, cancellationToken, options);

    public virtual Task<TaskResult<TEntity?>> DeleteAsync(Guid id, Guid? committedByUserId = default, CancellationToken cancellationToken = default, BaseDbEntityServiceOperationOptions? options = null)
        => DoDeleteAsync(id, committedByUserId, cancellationToken, options);

    public virtual Task<TaskResult<TEntity?>> UpdatePartialAsync(
        Guid id,
        Func<NhSetPropertyCalls<TUpdateMutateModel>, NhSetPropertyCalls<TUpdateMutateModel>> set,
        Action<NhSetPropertyCalls<TUpdateMutateModel>>? callsReady = null,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null,
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null
    ) => DoUpdatePartialAsync(id, set, callsReady, committedByUserId, beforeSave, cancellationToken, options);

    #endregion
}
