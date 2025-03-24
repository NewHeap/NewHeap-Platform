using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Services;

public interface IBaseDbEntityService<TEntity, TMutateModel> : IBaseDbEntityService<TEntity, TMutateModel, TEntity>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
{ 
    
}

public interface IBaseDbEntityService<TEntity, TMutateModel, TResult>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
    where TResult : class?
{
    Task<TaskResult<TResult?>> CreateAsync(TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);
    Task<TaskResult<TResult>> DeleteAsync(Guid id, Guid? committedByUserId = null, CancellationToken cancellationToken = default);
    Task<TResult?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    IRepository<TEntity> GetRepository();
    IQueryable<TEntity> QueryableWithAllIncludes(IQueryable<TEntity> queryable = null);
    Task<TaskResult<TResult>> UpdateAsync(Guid id, TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default);
    Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TResult, TEntity, TMutateModel> model, CancellationToken cancellationToken = default);
}

public abstract partial class BaseDbEntityService<TEntity, TMutateModel, TBaseDbEntityService> : BaseDbEntityService<TEntity, TMutateModel, TEntity, TBaseDbEntityService>, IBaseDbEntityService<TEntity, TMutateModel>
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
    ) 
        : base(repository, dbLogService, logHelperService, mapper, localizer, validationService, userManager)
    {
    }

    protected override TEntity EntityToResult(TEntity entity)
    {
        return entity;
    }
}


public abstract partial class BaseDbEntityService<TEntity, TMutateModel, TResult, TBaseDbEntityService> : IBaseDbEntityService<TEntity, TMutateModel, TResult> 
    where TEntity : class, IdDbEntity
    where TMutateModel : class
    where TResult : class?
    where TBaseDbEntityService : BaseDbEntityService<TEntity, TMutateModel, TResult, TBaseDbEntityService>
{
    protected readonly IStringLocalizer<TBaseDbEntityService> _localizer;
    protected readonly IRepository<TEntity> _repository;
    protected readonly DbLogService _dbLogService;
    protected readonly IMapper _mapper;
    protected readonly LogHelperService _logHelper;
    protected readonly ValidationService _validationService;
    protected readonly INhUserManager _userManager;

    public BaseDbEntityService(
        IRepository<TEntity> repository,
        DbLogService dbLogService,
        LogHelperService logHelperService,
        IMapper mapper,
        IStringLocalizer<TBaseDbEntityService> localizer,
        ValidationService validationService,
        INhUserManager userManager
        )
    {
        _repository = repository;
        _mapper = mapper;
        _dbLogService = dbLogService;
        _logHelper = logHelperService;
        _localizer = localizer;
        _validationService = validationService;
        _userManager = userManager;
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
    protected abstract TResult EntityToResult(TEntity entity);

    public virtual async Task<TResult?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return EntityToResult(await QueryableWithAllIncludes()
            .FirstOrDefaultAsync(m => m.Id == id));
    }

    public virtual async Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TResult, TEntity, TMutateModel> model, CancellationToken cancellationToken = default)
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

    public virtual async Task<TaskResult<TResult?>> CreateAsync(TMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default)
    {
        var result = new TaskResult<TResult?>();

        await ValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<TResult, TEntity, TMutateModel>(CRUDActionType.Create)
        {
            TaskResult = result,
            SourceModel = null,
            MutateModel = mutateModel,
        });

        if (!result.Success)
        {
            return result;
        }

        var entity = _mapper.Map<TEntity>(mutateModel);
        await _repository.AddAsync(entity);
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
            dbContext: _repository.Context
        );

        await _repository.SaveChangesAsync();

        result.Data = EntityToResult(entity);

        return result;
    }

    protected virtual Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperies(
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

    public virtual async Task<TaskResult<TResult>> UpdateAsync(
        Guid id,
        TMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null, 
        CancellationToken cancellationToken = default
        )
    {
        var result = new TaskResult<TResult>();

        var entity = await _repository
            .GetAll()
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.Id == id);

        await ValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<TResult, TEntity, TMutateModel>(CRUDActionType.Update)
        {
            TaskResult = result,
            SourceModel = entity,
            MutateModel = mutateModel,
        });

        if (!result.Success)
        {
            return result;
        }

        var originalData = LogHelperService.Copy(entity);

        entity = _mapper.Map(mutateModel, entity);
        entity!.LastModifiedDateTime = DateTimeOffset.UtcNow;
        beforeSave?.Invoke(entity);

        var updatedData = LogHelperService.Copy(entity);
        var changedProperties = await OnUpdateGetChangedProperies(originalData, updatedData);

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
                dbContext: _repository.Context
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
            dbContext: _repository.Context
        );

        await _repository.SaveChangesAsync();

        result.Data = EntityToResult(entity);

        return result;
    }

    public virtual async Task<TaskResult<TResult>> DeleteAsync(Guid id, Guid? committedByUserId = default, CancellationToken cancellationToken = default)
    {
        var result = new TaskResult<TResult>();

        var entity = await _repository
            .FindOneByAsync(x => x.Id == id);

        await ValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<TResult, TEntity, TMutateModel>(CRUDActionType.Delete)
        {
            TaskResult = result,
            SourceModel = entity,
            MutateModel = null,
        });

        if (!result.Success)
        {
            return result;
        }

        result.Data = EntityToResult(entity);
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
            dbContext: _repository.Context
        );

        await _repository.SaveChangesAsync();

        return result;
    }
    #endregion
}
