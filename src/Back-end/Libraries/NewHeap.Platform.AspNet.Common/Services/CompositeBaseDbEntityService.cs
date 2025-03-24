using AutoMapper;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;

namespace NewHeap.Platform.AspNet.Common.Services;

public interface ICompositeBaseDbEntityService<TEntity, TMutateModel> : IAbstractBaseDbEntityService<TEntity, TMutateModel>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
{
    Task<TaskResult<TEntity?>> CreateAsync(TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);
    Task<TaskResult<TEntity>> DeleteAsync(Guid id, Guid? committedByUserId = null, CancellationToken cancellationToken = default);
    Task<TEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    IRepository<TEntity> GetRepository();
    IQueryable<TEntity> QueryableWithAllIncludes(IQueryable<TEntity> queryable = null);
    Task<TaskResult<TEntity>> UpdateAsync(Guid id, TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);
    Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default);
}

public abstract partial class CompositeBaseDbEntityService<TEntity, TMutateModel, TCompositeBaseDbEntityService> : AbstractBaseDbEntityService<TEntity, TMutateModel, TCompositeBaseDbEntityService>, ICompositeBaseDbEntityService<TEntity, TMutateModel> 
    where TEntity : class, IdDbEntity
    where TMutateModel : class
    where TCompositeBaseDbEntityService : CompositeBaseDbEntityService<TEntity, TMutateModel, TCompositeBaseDbEntityService>
{
    protected CompositeBaseDbEntityService(
        IRepository<TEntity> repository, 
        DbLogService dbLogService, 
        LogHelperService logHelperService, 
        IMapper mapper, 
        IStringLocalizer<TCompositeBaseDbEntityService> localizer, 
        ValidationService validationService, 
        INhUserManager userManager
        ) : base(repository, dbLogService, logHelperService, mapper, localizer, validationService, userManager)
    {
    }

    #region TEntity

    public abstract Task<TEntity?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    public abstract Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default);

    public abstract Task<TaskResult<TEntity?>> CreateAsync(TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);

    public abstract Task<TaskResult<TEntity>> UpdateAsync(
        Guid id,
        TMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null,
        CancellationToken cancellationToken = default
        );

    public abstract Task<TaskResult<TEntity>> DeleteAsync(Guid id, Guid? committedByUserId = default, CancellationToken cancellationToken = default);
    #endregion
}
