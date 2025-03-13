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

public abstract partial class BaseDbEntityService<TEntity, TMutateModel, TViewModel, TBaseDbEntityService>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
    where TViewModel : class
    where TBaseDbEntityService : BaseDbEntityService<TEntity, TMutateModel, TViewModel, TBaseDbEntityService>
{
    protected readonly IStringLocalizer<TBaseDbEntityService> _localizer;
    protected readonly IRepository<TEntity> _repository;
    protected readonly DbLogService _dbLogService;
    protected readonly IMapper _mapper;
    protected readonly LogHelperService _logHelper;
    protected readonly ValidationService _validationService;
    protected readonly NhUserManager _userManager;

    public BaseDbEntityService(
        IRepository<TEntity> repository,
        DbLogService dbLogService,
        LogHelperService logHelperService,
        IMapper mapper,
        IStringLocalizer<TBaseDbEntityService> localizer,
        ValidationService validationService,
        NhUserManager userManager
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

    public virtual async Task<TEntity?> GetAsync(Guid id)
    {
        return await QueryableWithAllIncludes()
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public virtual async Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model)
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

    public virtual async Task<TaskResult<TEntity?>> CreateAsync(TMutateModel mutateModel, Guid? committedByUserId = null)
    {
        var result = new TaskResult<TEntity?>();

        await ValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel>(CRUDActionType.Create)
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

        await _dbLogService.LogAsync(
            message: "Entity create successful.",
            messageArguments: new string[] {},
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

        result.Data = entity;

        return result;
    }

    protected virtual Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperies(
        TEntity original,
        TEntity updated)
    {
        return _logHelper.ChangedProperties(original, updated, new Dictionary<Expression<Func<TEntity, object>>, Func<object, Task<string>>>
        {
            // Method resolvers
        }, []);
    }

    public virtual async Task<TaskResult<TEntity>> UpdateAsync(
        Guid id, 
        TMutateModel mutateModel, 
        Guid? committedByUserId = default, 
        Action<TEntity>? beforeSave = null
        )
    {
        var result = new TaskResult<TEntity>();

        var entity = await _repository
            .GetAll()
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.Id == id);

        await ValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel>(CRUDActionType.Update)
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

        result.Data = entity;

        return result;
    }

    public virtual async Task<TaskResult<TEntity>> DeleteAsync(Guid id, Guid? committedByUserId = default)
    {
        var result = new TaskResult<TEntity>();

        var address = await _repository
            .FindOneByAsync(x => x.Id == id);

        await ValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel>(CRUDActionType.Delete)
        {
            TaskResult = result,
            SourceModel = address,
            MutateModel = null,
        });

        if (!result.Success)
        {
            return result;
        }

        result.Data = address;
        _repository.Remove(address);

        await _dbLogService.LogAsync(
            message: "Entity remove successful.",
            messageArguments: new string[] {
                address.Id.ToString()
            },
            objectId: address.Id.ToString(),
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
