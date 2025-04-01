using AutoMapper;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;

namespace NewHeap.Platform.AspNet.Common.Services;

public interface ICompositeBaseDbEntityService<TEntity, TMutateModel, TViewModel> : IAbstractBaseDbEntityService<TEntity, TMutateModel>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
{
    Task<TaskResult<TViewModel?>> CreateAsync(TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);
    Task<TaskResult<TViewModel>> DeleteAsync(Guid id, Guid? committedByUserId = null, CancellationToken cancellationToken = default);
    Task<TViewModel?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TaskResult<TViewModel>> UpdateAsync(Guid id, TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);
    Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default);
}

public abstract partial class CompositeBaseDbEntityService<TEntity, TMutateModel, TViewModel, TCompositeBaseDbEntityService> : AbstractBaseDbEntityService<TEntity, TMutateModel, TCompositeBaseDbEntityService>, ICompositeBaseDbEntityService<TEntity, TMutateModel, TViewModel> 
    where TEntity : class, IdDbEntity
    where TMutateModel : class
    where TCompositeBaseDbEntityService : CompositeBaseDbEntityService<TEntity, TMutateModel, TViewModel, TCompositeBaseDbEntityService>
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

    public abstract Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default);

    public abstract Task<TaskResult<TViewModel?>> CreateAsync(TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);

    public abstract Task<TaskResult<TViewModel>> UpdateAsync(
        Guid id,
        TMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null,
        CancellationToken cancellationToken = default
        );

    public abstract Task<TaskResult<TViewModel>> DeleteAsync(Guid id, Guid? committedByUserId = default, CancellationToken cancellationToken = default);
    #endregion
}
