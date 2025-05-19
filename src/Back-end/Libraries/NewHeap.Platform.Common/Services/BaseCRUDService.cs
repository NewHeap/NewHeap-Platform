using AutoMapper;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Services;

public interface IBaseCRUDService<T, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel>
    where T : class
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
{
}

public interface IBaseCRUDService<T, TMutateModel> : IBaseCRUDService<T, TMutateModel, TMutateModel, TMutateModel>
    where T : class
    where TMutateModel : class
{
}

public abstract partial class BaseCRUDService<T, TMutateModel, TBaseCRUDService> : BaseCRUDService<T, TMutateModel, TMutateModel, TMutateModel, TBaseCRUDService>, IBaseCRUDService<T, TMutateModel>
    where T : class
    where TMutateModel : class
    where TBaseCRUDService : BaseCRUDService<T, TMutateModel, TBaseCRUDService>
{
    protected BaseCRUDService(
        LogHelperService logHelperService, 
        IMapper mapper, 
        IStringLocalizer<TBaseCRUDService> localizer, 
        ValidationService validationService) 
        : base(logHelperService, mapper, localizer, validationService)
    {
    }

    protected virtual async Task DoValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<T, T, TMutateModel> model, CancellationToken cancellationToken = default)
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

    protected sealed override Task DoValidateCreateAsync(CreateUpdateDeleteValidateModel<T, T, TMutateModel> model, CancellationToken cancellationToken = default)
    { 
        return DoValidateCreateUpdateDeleteAsync(model, cancellationToken);
    }

    protected sealed override Task DoValidateUpdateAsync(CreateUpdateDeleteValidateModel<T, T, TMutateModel> model, CancellationToken cancellationToken = default)
    {
        return DoValidateCreateUpdateDeleteAsync(model, cancellationToken);
    }

    protected sealed override Task DoValidateDeleteAsync(CreateUpdateDeleteValidateModel<T, T, TMutateModel> model, CancellationToken cancellationToken = default)
    { 
         return DoValidateCreateUpdateDeleteAsync(model, cancellationToken);
    }
}

public abstract partial class BaseCRUDService<T, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TBaseCRUDService> : IBaseCRUDService<T, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel>
    where T : class
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
    where TBaseCRUDService : BaseCRUDService<T, TCreateMutateModel, TUpdateMutateModel, TDeleteMutateModel, TBaseCRUDService>
{
    protected readonly IStringLocalizer<TBaseCRUDService> _localizer;
    protected readonly IMapper _mapper;
    protected readonly LogHelperService _logHelper;
    protected readonly ValidationService _validationService;

    public BaseCRUDService(
        LogHelperService logHelperService,
        IMapper mapper,
        IStringLocalizer<TBaseCRUDService> localizer,
        ValidationService validationService
        )
    {
        _mapper = mapper;
        _localizer = localizer;
        _logHelper = logHelperService;
        _validationService = validationService;
    }

    #region TEntity
    protected virtual Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperties(
        T original,
        T updated,
        CancellationToken cancellationToken = default
    )
    {
        return _logHelper.ChangedProperties(original, updated, new Dictionary<Expression<Func<T, object>>, Func<object, Task<string>>>
        {
            // Method resolvers
        }, [], []);
    }

    protected virtual async Task DoValidateCreateAsync(CreateUpdateDeleteValidateModel<T, T, TCreateMutateModel> model, CancellationToken cancellationToken = default)
    {
        _validationService.ValidateMutateModelModelState(model);
    }

    protected virtual async Task DoValidateUpdateAsync(CreateUpdateDeleteValidateModel<T, T, TUpdateMutateModel> model, CancellationToken cancellationToken = default)
    {
        if (model.SourceModel == null)
        {
            model.TaskResult.AddError(string.Empty, _localizer["Action type requires a source model."]);
        }

        if (model.TaskResult.Success)
        {
            _validationService.ValidateMutateModelModelState(model);
        }
    }

    protected virtual async Task DoValidateDeleteAsync(CreateUpdateDeleteValidateModel<T, T, TDeleteMutateModel> model, CancellationToken cancellationToken = default)
    {
        if (model.SourceModel == null)
        {
            model.TaskResult.AddError(string.Empty, _localizer["Action type requires a source model."]);
        }
    }

    protected abstract Task<T?> DoGetAsync(Guid id, CancellationToken cancellationToken = default);

    protected abstract Task<TaskResult<T?>> DoCreateAsync(TCreateMutateModel mutateModel, Guid? committedByUserId = null, Action<T>? beforeSave = null, CancellationToken cancellationToken = default);

    protected abstract Task<TaskResult<T>> DoUpdateAsync(
        Guid id,
        TUpdateMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<T>? beforeSave = null,
        CancellationToken cancellationToken = default
    );

    protected abstract Task<TaskResult<T>> DoDeleteAsync(Guid id, Guid? committedByUserId = default, CancellationToken cancellationToken = default);
    #endregion
}