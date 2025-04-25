using AutoMapper;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;

namespace NewHeap.Platform.AspNet.Common.Services;

public interface IBaseDbEntityService<TEntity, TMutateModel> : IBaseDbEntityService<TEntity, TMutateModel, TMutateModel, TMutateModel>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
{ 

}

public interface IBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel> : IAbstractBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel>
    where TEntity : class, IdDbEntity
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
{
    Task<TaskResult<TEntity?>> CreateAsync(TCreateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);
    Task<TaskResult<TEntity>> DeleteAsync(Guid id, Guid? committedByUserId = null, CancellationToken cancellationToken = default);
    Task<TEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TaskResult<TEntity>> UpdateAsync(Guid id, TUpdateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);
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

        async Task createUpdateCheck()
        {
            _validationService.ValidateMutateModelModelState(model);
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
        return DoValidateCreateUpdateDeleteAsync(model, cancellationToken);
    }

    protected sealed override Task DoValidateUpdateAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default)
    {
        return DoValidateCreateUpdateDeleteAsync(model, cancellationToken);
    }

    protected sealed override Task DoValidateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default)
    {
        return DoValidateCreateUpdateDeleteAsync(model, cancellationToken);
    }
}

public abstract partial class BaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TBaseDbEntityService> : AbstractBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TBaseDbEntityService>, IBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel> 
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
        => base.DoGetAsync(id, cancellationToken);

    protected virtual Task ValidateCreateAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TCreateMutateModel> model, CancellationToken cancellationToken = default)
        => base.DoValidateCreateAsync(model, cancellationToken);

    protected virtual Task ValidateUpdateAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TUpdateMutateModel> model, CancellationToken cancellationToken = default)
        => base.DoValidateUpdateAsync(model, cancellationToken);

    protected virtual Task ValidateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TDeleteMutateModel> model, CancellationToken cancellationToken = default)
        => base.DoValidateDeleteAsync(model, cancellationToken);

    public virtual Task<TaskResult<TEntity?>> CreateAsync(TCreateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default)
        => base.DoCreateAsync(mutateModel, committedByUserId, beforeSave, cancellationToken);

    public virtual Task<TaskResult<TEntity>> UpdateAsync(
        Guid id,
        TUpdateMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null,
        CancellationToken cancellationToken = default
        )
        => base.DoUpdateAsync(id, mutateModel, committedByUserId, beforeSave, cancellationToken);

    public virtual Task<TaskResult<TEntity>> DeleteAsync(Guid id, Guid? committedByUserId = default, CancellationToken cancellationToken = default)
        => base.DoDeleteAsync(id, committedByUserId, cancellationToken);

    #endregion
}
