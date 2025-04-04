using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Services;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Services;

public interface IAbstractBaseDbEntityService<TEntity, TMutateModel> : IBaseCRUDService<TEntity, TMutateModel>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
{
    IRepository<TEntity> GetRepository();
    IQueryable<TEntity> QueryableWithAllIncludes(IQueryable<TEntity> queryable = null);
}

public abstract partial class AbstractBaseDbEntityService<TEntity, TMutateModel, TAbstractBaseDbEntityService> : BaseCRUDService<TEntity, TMutateModel, TAbstractBaseDbEntityService>, IAbstractBaseDbEntityService<TEntity, TMutateModel>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
    where TAbstractBaseDbEntityService : AbstractBaseDbEntityService<TEntity, TMutateModel, TAbstractBaseDbEntityService>
{
    protected readonly IRepository<TEntity> _repository;
    protected readonly INhDbLogService _dbLogService;

    public AbstractBaseDbEntityService(
        IRepository<TEntity> repository,
        INhDbLogService dbLogService,
        LogHelperService logHelperService,
        IMapper mapper,
        IStringLocalizer<TAbstractBaseDbEntityService> localizer,
        ValidationService validationService
        )
        : base(logHelperService, mapper, localizer, validationService)
    {
        _repository = repository;
        _dbLogService = dbLogService;
    }

    public IRepository<TEntity> GetRepository()
    {
        return _repository;
    }

    public virtual IQueryable<TEntity> QueryableWithAllIncludes(IQueryable<TEntity> queryable = null)
    {
        queryable ??= _repository
            .GetAll()
        ;

        return queryable;
    }

    #region TEntity
    protected override async Task<TEntity?> DoGetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await QueryableWithAllIncludes()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
    }

    protected override async Task DoValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default)
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

    protected override async Task<TaskResult<TEntity?>> DoCreateAsync(TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default)
    {
        var result = new TaskResult<TEntity?>();

        await DoValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel>(CRUDActionType.Create)
        {
            TaskResult = result,
            SourceModel = null,
            MutateModel = mutateModel,
        }, cancellationToken);

        if (!result.Success)
        {
            return result;
        }

        var entity = _mapper.Map<TEntity>(mutateModel);
        await _repository.AddAsync(entity, cancellationToken);
        beforeSave?.Invoke(entity);

        await _dbLogService.LogAsync(
            message: "Entity create successful.",
            messageArguments: new string[] { },
            objectId: entity.Id.ToString(),
            objectType: (typeof(TEntity)).Name,
            objectTypeFull: (typeof(TEntity)).FullName,
            userId: committedByUserId,
            action: LogAction.Create,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _repository.Context,
            cancellationToken: cancellationToken
        );

        await _repository.SaveChangesAsync(cancellationToken);

        result.Data = entity;

        return result;
    }

    protected override Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperies(
        TEntity original,
        TEntity updated, 
        CancellationToken cancellationToken = default
        )
    {
        return _logHelper.ChangedProperties(original, updated, new Dictionary<Expression<Func<TEntity, object>>, Func<object, Task<string>>>
        {
            // Method resolvers
        }, []);
    }

    protected override async Task<TaskResult<TEntity>> DoUpdateAsync(
        Guid id,
        TMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null, 
        CancellationToken cancellationToken = default
        )
    {
        var result = new TaskResult<TEntity>();

        var entity = await _repository
            .GetAll()
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        await DoValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel>(CRUDActionType.Update)
        {
            TaskResult = result,
            SourceModel = entity,
            MutateModel = mutateModel,
        }, cancellationToken);

        if (!result.Success)
        {
            return result;
        }

        var originalData = LogHelperService.Copy(entity);

        entity = _mapper.Map(mutateModel, entity);
        entity!.LastModifiedDateTime = DateTimeOffset.UtcNow;
        beforeSave?.Invoke(entity);

        var updatedData = LogHelperService.Copy(entity);
        var changedProperties = await OnUpdateGetChangedProperies(originalData, updatedData, cancellationToken);

        if (changedProperties.Any())
        {
            var values = string.Join("\n", changedProperties.Select(x => $"{x.Key}: '{x.OriginalValue}' -> '{x.UpdateValue}'"));
            await _dbLogService.LogAsync(
                "Entity values updated",
                messageArguments: new string[]
                {
                    values
                },
                objectId: entity.Id.ToString(),
                objectType: typeof(TEntity).Name,
                objectTypeFull: typeof(TEntity).FullName,
                userId: committedByUserId,
                action: LogAction.Update,
                type: LogType.Information,
                source: LogSource.Internal,
                tag: GetType().Name,
                doSaveChanges: false,
                dbContext: _repository.Context,
                cancellationToken: cancellationToken
            );
        }

        await _dbLogService.LogAsync(
            message: "Entity update successful.",
            messageArguments: new string[] {
            },
            objectId: entity.Id.ToString(),
            objectType: (typeof(TEntity)).Name,
            objectTypeFull: (typeof(TEntity)).FullName,
            userId: committedByUserId,
            action: LogAction.Update,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _repository.Context,
            cancellationToken: cancellationToken
        );

        await _repository.SaveChangesAsync(cancellationToken);

        result.Data = entity;

        return result;
    }

    protected override async Task<TaskResult<TEntity>> DoDeleteAsync(Guid id, Guid? committedByUserId = default, CancellationToken cancellationToken = default)
    {
        var result = new TaskResult<TEntity>();

        var entity = await _repository
            .FindOneByAsync(x => x.Id == id, cancellationToken);

        await DoValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel>(CRUDActionType.Delete)
        {
            TaskResult = result,
            SourceModel = entity,
            MutateModel = null,
        }, cancellationToken);

        if (!result.Success)
        {
            return result;
        }

        result.Data = entity;
        _repository.Remove(entity);

        await _dbLogService.LogAsync(
            message: "Entity remove successful.",
            messageArguments: new string[] {
                entity.Id.ToString()
            },
            objectId: entity.Id.ToString(),
            objectType: (typeof(TEntity)).Name,
            objectTypeFull: (typeof(TEntity)).FullName,
            userId: committedByUserId,
            action: LogAction.Delete,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _repository.Context,
            cancellationToken: cancellationToken
        );

        await _repository.SaveChangesAsync(cancellationToken);

        return result;
    }
    #endregion
}
