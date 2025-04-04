using AutoMapper;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Services;

public interface IBaseCRUDService<T, TMutateModel>
    where T : class
    where TMutateModel : class
{
}

public abstract partial class BaseCRUDService<T, TMutateModel, TBaseCRUDService> : IBaseCRUDService<T, TMutateModel> 
    where T : class
    where TMutateModel : class
    where TBaseCRUDService : BaseCRUDService<T, TMutateModel, TBaseCRUDService>
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

    protected virtual Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperies(
        T original,
        T updated,
        CancellationToken cancellationToken = default
    )
    {
        return _logHelper.ChangedProperties(original, updated, new Dictionary<Expression<Func<T, object>>, Func<object, Task<string>>>
        {
            // Method resolvers
        }, []);
    }

    protected abstract Task<T?> DoGetAsync(Guid id, CancellationToken cancellationToken = default);


    protected abstract Task<TaskResult<T?>> DoCreateAsync(TMutateModel mutateModel, Guid? committedByUserId = null, Action<T>? beforeSave = null, CancellationToken cancellationToken = default);



    protected abstract Task<TaskResult<T>> DoUpdateAsync(
        Guid id,
        TMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<T>? beforeSave = null,
        CancellationToken cancellationToken = default
    );

    protected abstract Task<TaskResult<T>> DoDeleteAsync(Guid id, Guid? committedByUserId = default, CancellationToken cancellationToken = default);
    #endregion
}
