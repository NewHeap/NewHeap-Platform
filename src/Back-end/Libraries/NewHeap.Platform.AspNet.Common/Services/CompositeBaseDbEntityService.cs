using AutoMapper;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;

namespace NewHeap.Platform.AspNet.Common.Services;

public interface ICompositeBaseDbEntityService<TEntity, TMutateModel, TViewModel> : ICompositeBaseDbEntityService<TEntity, TMutateModel, TMutateModel, TMutateModel, TViewModel>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
{
}

public interface ICompositeBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TViewModel> : IAbstractBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel>
    where TEntity : class, IdDbEntity
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
{
    Task<TaskResult<TViewModel?>> CreateAsync(TCreateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);
    Task<TaskResult<TViewModel>> DeleteAsync(Guid id, Guid? committedByUserId = null, CancellationToken cancellationToken = default);
    Task<TViewModel?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TaskResult<TViewModel>> UpdateAsync(Guid id, TUpdateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);
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
}

public abstract partial class CompositeBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TViewModel, TCompositeBaseDbEntityService> : AbstractBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TCompositeBaseDbEntityService>, ICompositeBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TViewModel> 
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

    public abstract Task<TaskResult<TViewModel?>> CreateAsync(TCreateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);

    public abstract Task<TaskResult<TViewModel>> UpdateAsync(
        Guid id,
        TUpdateMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null,
        CancellationToken cancellationToken = default
        );

    public abstract Task<TaskResult<TViewModel>> DeleteAsync(Guid id, Guid? committedByUserId = default, CancellationToken cancellationToken = default);
    #endregion
}
