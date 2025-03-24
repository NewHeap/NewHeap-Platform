using AutoMapper;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;

namespace NewHeap.Platform.AspNet.Common.Services;

public interface IBaseDbEntityService<TEntity, TMutateModel> : IAbstractBaseDbEntityService<TEntity, TMutateModel>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
{
    Task<TaskResult<TEntity?>> CreateAsync(TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);
    Task<TaskResult<TEntity>> DeleteAsync(Guid id, Guid? committedByUserId = null, CancellationToken cancellationToken = default);
    Task<TEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TaskResult<TEntity>> UpdateAsync(Guid id, TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);
    Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default);
}

public abstract partial class BaseDbEntityService<TEntity, TMutateModel, TBaseDbEntityService> : AbstractBaseDbEntityService<TEntity, TMutateModel, TBaseDbEntityService>, IBaseDbEntityService<TEntity, TMutateModel> 
    where TEntity : class, IdDbEntity
    where TMutateModel : class
    where TBaseDbEntityService : BaseDbEntityService<TEntity, TMutateModel, TBaseDbEntityService>
{
    protected BaseDbEntityService(
        IRepository<TEntity> repository, 
        DbLogService dbLogService, 
        LogHelperService logHelperService, 
        IMapper mapper, 
        IStringLocalizer<TBaseDbEntityService> localizer, 
        ValidationService validationService, 
        INhUserManager userManager
        ) : base(repository, dbLogService, logHelperService, mapper, localizer, validationService, userManager)
    {
    }

    #region TEntity

    public virtual Task<TEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default) 
        => base.DoGetAsync(id, cancellationToken);

    public virtual Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default)
        => base.DoValidateCreateUpdateDeleteAsync(model, cancellationToken);

    public virtual Task<TaskResult<TEntity?>> CreateAsync(TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default)
        => base.DoCreateAsync(mutateModel, committedByUserId, beforeSave, cancellationToken);

    public virtual Task<TaskResult<TEntity>> UpdateAsync(
        Guid id,
        TMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null,
        CancellationToken cancellationToken = default
        )
        => base.DoUpdateAsync(id, mutateModel, committedByUserId, beforeSave, cancellationToken);

    public virtual Task<TaskResult<TEntity>> DeleteAsync(Guid id, Guid? committedByUserId = default, CancellationToken cancellationToken = default)
        => base.DoDeleteAsync(id, committedByUserId, cancellationToken);

    #endregion
}
