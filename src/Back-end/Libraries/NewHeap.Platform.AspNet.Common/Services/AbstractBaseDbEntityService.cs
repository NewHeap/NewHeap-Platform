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
using NewHeap.Platform.Common.Utilities;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Services;

public interface IAbstractBaseDbEntityServiceOperationOptions : IBaseCRUDServiceOperationOptions
{
    bool SaveChangesDisabled { get; set; }
    bool DbLoggingDisabled { get; set; }
}

public class AbstractBaseDbEntityServiceOperationOptions : BaseCRUDServiceOperationOptions, IAbstractBaseDbEntityServiceOperationOptions
{
    public bool SaveChangesDisabled { get; set; } = false;
    public bool DbLoggingDisabled { get; set; } = false;
}

public interface IAbstractBaseDbEntityService<TEntity, TMutateModel, TOperationOptions> : IAbstractBaseDbEntityService<TEntity, TMutateModel, TMutateModel, TMutateModel, TOperationOptions>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
    where TOperationOptions : class, IAbstractBaseDbEntityServiceOperationOptions
{

}

public interface IAbstractBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TOperationOptions> : IBaseCRUDService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TOperationOptions>
    where TEntity : class, IdDbEntity
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
    where TOperationOptions : class, IAbstractBaseDbEntityServiceOperationOptions
{
    IRepository<TEntity> GetRepository();
    IQueryable<TEntity> QueryableWithAllIncludes(IQueryable<TEntity>? queryable = null);
    IQueryable<TEntity> QueryableWithUpdateDeleteIncludes(IQueryable<TEntity>? queryable = null);
}

public abstract partial class AbstractBaseDbEntityService<TEntity, TMutateModel, TAbstractBaseDbEntityService, TOperationOptions> : AbstractBaseDbEntityService<TEntity, TMutateModel, TMutateModel, TMutateModel, TAbstractBaseDbEntityService, TOperationOptions>, IAbstractBaseDbEntityService<TEntity, TMutateModel, TOperationOptions>
    where TEntity : class, IdDbEntity
    where TMutateModel : class
    where TAbstractBaseDbEntityService : AbstractBaseDbEntityService<TEntity, TMutateModel, TAbstractBaseDbEntityService, TOperationOptions>
    where TOperationOptions : class, IAbstractBaseDbEntityServiceOperationOptions
{
    protected AbstractBaseDbEntityService(
        IRepository<TEntity> repository, 
        INhDbLogService dbLogService, 
        LogHelperService logHelperService, 
        IMapper mapper, 
        IStringLocalizer<TAbstractBaseDbEntityService> localizer, 
        ValidationService validationService) : base(repository, dbLogService, logHelperService, mapper, localizer, validationService)
    {
    }

    protected virtual Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<TEntity, TEntity, TMutateModel> model, CancellationToken cancellationToken = default)
    { 
        return DoValidateCreateUpdateDeleteAsync(model, cancellationToken);
    }

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

public abstract partial class AbstractBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TAbstractBaseDbEntityService, TOperationOptions> : BaseCRUDService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TAbstractBaseDbEntityService, TOperationOptions>, IAbstractBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TOperationOptions>
    where TEntity : class, IdDbEntity
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
    where TAbstractBaseDbEntityService : AbstractBaseDbEntityService<TEntity, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TAbstractBaseDbEntityService, TOperationOptions>
    where TOperationOptions : class, IAbstractBaseDbEntityServiceOperationOptions
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

    public virtual IQueryable<TEntity> QueryableWithAllIncludes(IQueryable<TEntity>? queryable = null)
    {
        queryable ??= _repository
            .GetAll()
        ;

        return queryable;
    }

    public virtual IQueryable<TEntity> QueryableWithUpdateDeleteIncludes(IQueryable<TEntity>? queryable = null)
    {
        queryable ??= _repository
            .GetAll()
        ;

        return queryable;
    }

    protected Expression<Func<TEntity, bool>> GetEntityExistsExpression(Guid? id)
    {
        Expression<Func<TEntity, bool>> expr = x => ((id.HasValue) ? x.Id != id.Value : true);
        return expr;
    }

    #region TEntity
    protected override Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperties(
        TEntity? original,
        TEntity? updated,
        CancellationToken cancellationToken = default
        )
    {
        return _logHelper.ChangedProperties(original, updated, new Dictionary<Expression<Func<TEntity?, object?>>, Func<object?, Task<string?>>>
        {
            // Method resolvers
        }, [], []);
    }
    protected override async Task<TEntity?> DoGetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await QueryableWithAllIncludes()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
    }

    protected override async Task<TaskResult<TEntity?>> DoCreateAsync(TCreateMutateModel mutateModel, Guid? committedByUserId = null, Action<TEntity>? beforeSave = null, CancellationToken cancellationToken = default, TOperationOptions? options = null)
    {
        var result = new TaskResult<TEntity?>();

        await ValidateCreateAsync(new CreateUpdateDeleteValidateModel<TEntity, TEntity, TCreateMutateModel>(CRUDActionType.Create)
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
        entity.CreationDateTime = DateTimeOffset.UtcNow;
        entity.LastModifiedDateTime = DateTimeOffset.UtcNow;

        if (options?.DbLoggingDisabled != true)
        {
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
        }

        if (options?.SaveChangesDisabled != true)
        {
            await _repository.SaveChangesAsync(cancellationToken);
        }

        result.Data = entity;

        return result;
    }

    protected override async Task<TaskResult<TEntity?>> DoUpdateAsync(
        Guid id,
        TUpdateMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null, 
        CancellationToken cancellationToken = default,
        TOperationOptions? options = null
        )
    {
        var result = new TaskResult<TEntity?>();

        var entity = await QueryableWithUpdateDeleteIncludes()
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        await ValidateUpdateAsync(new CreateUpdateDeleteValidateModel<TEntity, TEntity, TUpdateMutateModel>(CRUDActionType.Update)
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

        if (options?.DbLoggingDisabled != true)
        {
            var updatedData = LogHelperService.Copy(entity);
            var changedProperties = await OnUpdateGetChangedProperties(originalData, updatedData, cancellationToken);

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
        }

        if (options?.SaveChangesDisabled != true)
        {
            await _repository.SaveChangesAsync(cancellationToken);
        }

        result.Data = entity;

        return result;
    }

    protected override async Task<TaskResult<TEntity?>> DoUpdatePartialAsync(
        Guid id,
        Func<NhSetPropertyCalls<TUpdateMutateModel>, NhSetPropertyCalls<TUpdateMutateModel>> set,
        Action<NhSetPropertyCalls<TUpdateMutateModel>>? callsReady = null,
        Guid? committedByUserId = default,
        Action<TEntity>? beforeSave = null,
        CancellationToken cancellationToken = default,
        TOperationOptions? options = null
    )
    {
        var result = new TaskResult<TEntity?>();

        var entity = await QueryableWithUpdateDeleteIncludes()
            .OrderBy(x => x.Id)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity == null)
        {
            return result.WithKeylessError(_localizer["EntityNotFound", id]);
        }

        var mutateModel = _mapper.Map<TUpdateMutateModel>(entity);

        var calls = set(new NhSetPropertyCalls<TUpdateMutateModel>());
        calls.Apply(mutateModel);
        callsReady?.Invoke(calls);

        using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);

        var updateResult = await DoUpdateAsync(
             id,
             mutateModel,
             committedByUserId,
             beforeSave,
             cancellationToken,
             options
        );

        if (!updateResult.Success)
        {
            updateResult.ApplyTo(result);
            return result;
        }

        await transaction.CommitAsync(cancellationToken);

        return result;
    }

    protected override async Task<TaskResult<TEntity?>> DoDeleteAsync(Guid id, Guid? committedByUserId = default, CancellationToken cancellationToken = default, TOperationOptions? options = null)
    {
        var result = new TaskResult<TEntity?>();

        var entity = await QueryableWithUpdateDeleteIncludes()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        await ValidateDeleteAsync(new CreateUpdateDeleteValidateModel<TEntity, TEntity, TDeleteMutateModel>(CRUDActionType.Delete)
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
        _repository.Remove(entity!);

        if (options?.DbLoggingDisabled != true)
        {
            await _dbLogService.LogAsync(
                message: "Entity remove successful.",
                messageArguments: new string[] {
                    entity!.Id.ToString()
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
        }

        if (options?.SaveChangesDisabled != true)
        {
            await _repository.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
    #endregion

    protected static TaskResult<string> GetPropertyNameFromExpression<T>(Expression<Func<T, object>> expression)
    {
        var result = new TaskResult<string>();

        if (expression.Body is MemberExpression member)
        { 
            return result.WithData(member.Member.Name);
        }

        if (expression.Body is UnaryExpression unary &&
            unary.Operand is MemberExpression unaryMember)
        { 
            return result.WithData(unaryMember.Member.Name);
        }

        return result.WithKeylessError("Invalid expression. Only simple member access expressions are allowed.");
    }
}
