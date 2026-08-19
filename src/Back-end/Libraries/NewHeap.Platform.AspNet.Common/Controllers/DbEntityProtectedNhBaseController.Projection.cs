using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Exceptions;
using NewHeap.Platform.Common.Models;
using System.ComponentModel;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract partial class DbEntityProtectedNhBaseController<
    TDbEntity,
    TCreateMutateModel,
    TUpdateMutateModel,
    TDeleteMutateModel,
    TViewModel,
    TBaseDbEntityService,
    TCollectionRequestModel>
    where TDbEntity : class, IdDbEntity
    where TCreateMutateModel : class
    where TUpdateMutateModel : class
    where TDeleteMutateModel : class
    where TViewModel : class
    where TBaseDbEntityService : IBaseDbEntityService<
        TDbEntity,
        TCreateMutateModel,
        TUpdateMutateModel,
        TDeleteMutateModel>
    where TCollectionRequestModel : CollectionRequestModel, new()
{
    [NonAction]
    protected virtual void ConfigureProjectedCollectionProcessing(
        CollectionProcessingOptionsBuilder<TViewModel, TViewModel> options)
    {
    }

    [NonAction]
    protected virtual void ConfigureProjectedCollectionProcessing<TCustomViewModel>(
        CollectionProcessingOptionsBuilder<TCustomViewModel, TCustomViewModel> options)
        where TCustomViewModel : class
    {
        if (typeof(TCustomViewModel) == typeof(TViewModel))
        {
            ConfigureProjectedCollectionProcessing(
                (CollectionProcessingOptionsBuilder<TViewModel, TViewModel>)(object)options);
        }
    }

    [NonAction]
    protected override void ConfigureProjectedCollectionProcessing<TEntity, TCustomViewModel>(
        CollectionProcessingOptionsBuilder<TCustomViewModel, TCustomViewModel> options)
    {
        base.ConfigureProjectedCollectionProcessing<TEntity, TCustomViewModel>(options);

        if (typeof(TEntity) == typeof(TDbEntity))
        {
            ConfigureProjectedCollectionProcessing(options);
        }
    }

    [NonAction]
    protected virtual Task<IActionResult> DoGetProjected<TCustomViewModel>(
        TCollectionRequestModel requestModel,
        Expression<Func<TDbEntity, TCustomViewModel>> projection,
        IQueryable<TDbEntity>? overrideQuery = null,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TCustomViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TCustomViewModel : class
    {
        return DoGetProjected(
            requestModel,
            projection,
            configureOptions: null,
            overrideQuery,
            asNoTracking: true,
            cancellationToken,
            defaultOrderBy);
    }

    [NonAction]
    protected virtual async Task<IActionResult> DoGetProjected<TCustomViewModel>(
        TCollectionRequestModel requestModel,
        Expression<Func<TDbEntity, TCustomViewModel>> projection,
        Action<CollectionProcessingOptionsBuilder<TCustomViewModel, TCustomViewModel>>? configureOptions,
        IQueryable<TDbEntity>? overrideQuery = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TCustomViewModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TCustomViewModel : class
    {
        requestModel ??= new TCollectionRequestModel();
        var query = overrideQuery ?? await GetQueryableAsync(cancellationToken);

        if (defaultOrderBy.Length == 0)
        {
            defaultOrderBy = GetDefaultProjectedCollectionResultOrderBy<TCustomViewModel>();
        }

        try
        {
            var result = await _httpCollectionProcessingService.GetProjectedCollectionResultModelAsync(
                requestModel,
                query,
                projection,
                options =>
                {
                    ConfigureProjectedCollectionProcessing<TDbEntity, TCustomViewModel>(options);
                    configureOptions?.Invoke(options);
                },
                resultQueryableFunc: null,
                asNoTracking,
                cancellationToken,
                defaultOrderBy);

            return Ok(result);
        }
        catch (InvalidFilterCollectionResultException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return BadRequest(ModelState);
        }
    }

    [NonAction]
    protected virtual (
        Expression<Func<TCustomViewModel, object>> orderByKey,
        ListSortDirection sortDirection)[] GetDefaultProjectedCollectionResultOrderBy<TCustomViewModel>()
        where TCustomViewModel : class
    {
        var result = new List<(
            Expression<Func<TCustomViewModel, object>> orderByKey,
            ListSortDirection sortDirection)>();

        foreach (var defaultOrderBy in GetDefaultCollectionResultOrderBy())
        {
            var memberNames = GetMemberNames(defaultOrderBy.orderByKey.Body);

            if (memberNames.Count == 0)
            {
                continue;
            }

            var parameter = Expression.Parameter(typeof(TCustomViewModel), "viewModel");
            Expression member = parameter;

            try
            {
                foreach (var memberName in memberNames)
                {
                    member = Expression.PropertyOrField(member, memberName);
                }
            }
            catch (ArgumentException)
            {
                continue;
            }

            result.Add((
                Expression.Lambda<Func<TCustomViewModel, object>>(
                    Expression.Convert(member, typeof(object)),
                    parameter),
                defaultOrderBy.sortDirection));
        }

        return result.ToArray();
    }

    private static IReadOnlyList<string> GetMemberNames(Expression expression)
    {
        expression = StripConvert(expression);
        var memberNames = new Stack<string>();

        while (expression is MemberExpression memberExpression)
        {
            memberNames.Push(memberExpression.Member.Name);
            expression = StripConvert(memberExpression.Expression!);
        }

        return expression is ParameterExpression
            ? memberNames.ToArray()
            : [];
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression unaryExpression &&
               (unaryExpression.NodeType == ExpressionType.Convert ||
                unaryExpression.NodeType == ExpressionType.ConvertChecked))
        {
            expression = unaryExpression.Operand;
        }

        return expression;
    }
}
