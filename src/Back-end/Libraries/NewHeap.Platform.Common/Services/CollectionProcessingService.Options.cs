using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Exceptions;
using NewHeap.Platform.Common.Extensions;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Utilities;
using System.Collections;
using System.ComponentModel;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.Common.Services;

public partial class CollectionProcessingService
{
    public virtual Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return _GetCollectionResultModelAsync(
            requestModel,
            queryable,
            CreateCollectionProcessingOptions(configureOptions),
            [.. defaultOrderBy],
            resultQueryableFunc,
            asNoTracking,
            cancellationToken);
    }

    public virtual Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return _GetSimpleCollectionResultModelAsync(
            requestModel,
            queryable,
            CreateCollectionProcessingOptions(configureOptions),
            [.. defaultOrderBy],
            resultQueryableFunc,
            asNoTracking,
            cancellationToken);
    }

    public virtual Task<(IQueryable<TEntity> queryable, long totalCount, List<FilterCollectionRequestModel> filterResult, List<OrderByCollectionRequestModel> orderByResult)> ProcessQueryable<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        CollectionProcessingOptions<TEntity, TViewModel> options,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return _ProcessQueryable(
            requestModel,
            queryable,
            options,
            [.. defaultOrderBy],
            cancellationToken);
    }

    public void ProcessSearch<TEntity, TViewModel>(
        ref IQueryable<TEntity> queryable,
        string? qSearch,
        CollectionProcessingOptions<TEntity, TViewModel> options)
        where TEntity : class
        where TViewModel : class
    {
        if (string.IsNullOrWhiteSpace(qSearch))
        {
            return;
        }

        Expression<Func<TEntity, bool>>? searchExpression = null;
        var parameter = Expression.Parameter(typeof(TEntity), "x");

        if (options.UseSearchableAttributes)
        {
            AddAttributeSearchExpressions<TEntity>(typeof(TViewModel), parameter, ref searchExpression, qSearch);
        }

        foreach (var selector in options.SearchableSelectors)
        {
            AddConfiguredSearchExpression(parameter, selector, ref searchExpression, qSearch);
        }

        if (searchExpression != null)
        {
            queryable = queryable.Where(searchExpression);
        }
    }

    protected async Task<CollectionResultModel<TViewModel>> _GetCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        CollectionProcessingOptions<TEntity, TViewModel> options,
        List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class
    {
        var defaultItemsPerPage = GetDefaultItemsPerPage();
        var defaultMaxItemsPerPage = GetDefaultMaxItemsPerPage();

        if (requestModel.ItemsPerPage < 1)
        {
            requestModel.ItemsPerPage = defaultItemsPerPage;
        }

        if (requestModel.ItemsPerPage > defaultMaxItemsPerPage)
        {
            requestModel.ItemsPerPage = defaultMaxItemsPerPage;
        }

        if (requestModel.Page < 1)
        {
            requestModel.Page = 1;
        }

        if (asNoTracking)
        {
            queryable = queryable.AsNoTracking();
        }

        var processedResult = await _ProcessQueryable(
            requestModel,
            queryable,
            options,
            defaultOrderBy,
            cancellationToken);

        queryable = processedResult.queryable;

        if (resultQueryableFunc != null)
        {
            queryable = await resultQueryableFunc.Invoke(queryable, cancellationToken);
        }

        var dbItems = queryable.GetType().GetInterfaces().Contains(typeof(IAsyncEnumerable<TEntity>))
            ? await queryable.ToListAsync(cancellationToken)
            : queryable.ToList();

        var items = typeof(TViewModel).Equals(typeof(TEntity))
            ? (List<TViewModel>)(object)dbItems
            : _mapper.Map<List<TViewModel>>(dbItems);

        return new CollectionResultModel<TViewModel>
        {
            Page = requestModel.Page,
            ItemsPerPage = requestModel.ItemsPerPage,
            OrderBy = processedResult.orderByResult,
            Filter = processedResult.filterResult,
            TotalCount = processedResult.totalCount,
            ResultCount = items.Count,
            Search = requestModel.Search,
            Items = items
        };
    }

    protected async Task<SimpleCollectionResultModel<TViewModel>> _GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        CollectionProcessingOptions<TEntity, TViewModel> options,
        List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class
    {
        var resultModel = await _GetCollectionResultModelAsync(
            requestModel,
            queryable,
            options,
            defaultOrderBy,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken);

        return new SimpleCollectionResultModel<TViewModel>
        {
            Page = requestModel.Page,
            ItemsPerPage = requestModel.ItemsPerPage,
            TotalCount = resultModel.TotalCount,
            ResultCount = resultModel.ResultCount,
            Items = resultModel.Items
        };
    }

    protected async Task<(IQueryable<TEntity> queryable, long totalCount, List<FilterCollectionRequestModel> filterResult, List<OrderByCollectionRequestModel> orderByResult)> _ProcessQueryable<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        CollectionProcessingOptions<TEntity, TViewModel> options,
        List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy,
        CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class
    {
        ProcessSearch(ref queryable, requestModel.Search?.Trim(), options);
        var filterResult = ProcessFilter(ref queryable, requestModel.Filter, options);
        var orderByResult = ProcessOrderBy(ref queryable, requestModel.OrderBy, options, defaultOrderBy);

        var totalCount = queryable.GetType().GetInterfaces().Contains(typeof(IAsyncEnumerable<TEntity>))
            ? await queryable.LongCountAsync(cancellationToken)
            : queryable.LongCount();

        queryable = queryable
            .PageSkipTake(requestModel)
            .AsQueryable();

        return (queryable, totalCount, filterResult, orderByResult);
    }

    protected List<FilterCollectionRequestModel> ProcessFilter<TEntity, TViewModel>(
        ref IQueryable<TEntity> queryable,
        List<FilterCollectionRequestModel>? filterCollection,
        CollectionProcessingOptions<TEntity, TViewModel> options)
        where TEntity : class
        where TViewModel : class
    {
        var filterResult = new List<FilterCollectionRequestModel>();

        if (filterCollection == null || !filterCollection.Any())
        {
            return filterResult;
        }

        var filterProperties = options.UseFilterableAttributes
            ? typeof(TViewModel).GetProperties().Where(prop => prop.IsDefined(typeof(FilterableAttribute), false)).ToArray()
            : [];

        if (!filterProperties.Any() && !options.FilterableSelectors.Any())
        {
            return filterResult;
        }

        foreach (var filter in filterCollection)
        {
            if (!IsFilterAllowed(filter, filterProperties, options))
            {
                throw new InvalidFilterCollectionResultException($"Invalid filter field '{filter.Key}'");
            }

            var mappedFilter = MapFilterKeys(filter, options.FilterableSelectors);
            var filterLambda = GetFilterLambda<TEntity>(mappedFilter, filterProperties, skipValidation: true);
            if (filterLambda != null)
            {
                queryable = queryable.Where(filterLambda);
            }

            filterResult.Add(filter);
        }

        return filterResult;
    }

    protected List<OrderByCollectionRequestModel> ProcessOrderBy<TEntity, TViewModel>(
        ref IQueryable<TEntity> queryable,
        List<OrderByCollectionRequestModel>? orderByCollection,
        CollectionProcessingOptions<TEntity, TViewModel> options,
        List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        var orderByResult = new List<OrderByCollectionRequestModel>();

        if (orderByCollection != null && orderByCollection.Any())
        {
            var orderByProperties = options.UseOrderableAttributes
                ? typeof(TViewModel).GetProperties().Where(prop => prop.IsDefined(typeof(OrderableAttribute), false)).ToArray()
                : [];

            foreach (var orderBy in orderByCollection)
            {
                orderBy.Direction = (orderBy.Direction ?? "ASC").ToUpper();

                if (string.IsNullOrWhiteSpace(orderBy.Key))
                {
                    continue;
                }

                var hasConfiguredOrderBy = TryGetConfiguredOrderPath(options.OrderableSelectors, orderBy.Key, out var memberPath);
                var mappedKey = hasConfiguredOrderBy
                    ? memberPath
                    : orderBy.Key;

                if (!hasConfiguredOrderBy && !IsOrderByAllowed<TViewModel>(mappedKey, orderByProperties, options.UseOrderableAttributes))
                {
                    continue;
                }

                if (!orderBy.Direction.Equals("ASC") && !orderBy.Direction.Equals("DESC"))
                {
                    orderBy.Direction = "ASC";
                }

                orderByResult.Add(new OrderByCollectionRequestModel
                {
                    Key = orderBy.Key,
                    Direction = orderBy.Direction,
                    Method = orderBy.Method
                });
            }

            if (orderByResult.Any())
            {
                var count = 0;
                foreach (var orderByItem in orderByResult)
                {
                    count++;

                    var mappedKey = TryGetConfiguredOrderPath(options.OrderableSelectors, orderByItem.Key, out var memberPath)
                        ? memberPath
                        : orderByItem.Key;

                    var orderByKey = BuildOrderByKey(mappedKey);

                    switch (orderByItem.Method)
                    {
                        case OrderByMethod.Default:
                            {
                                if (count <= 1 || queryable is not IOrderedQueryable<TEntity> orderedQueryable)
                                {
                                    queryable = queryable.OrderBy($"{orderByKey} {orderByItem.Direction}");
                                }
                                else
                                {
                                    queryable = orderedQueryable.ThenBy($"{orderByKey} {orderByItem.Direction}");
                                }

                                break;
                            }
                        case OrderByMethod.Natural:
                            {
                                if (count <= 1 || queryable is not IOrderedQueryable<TEntity> orderedQueryable)
                                {
                                    queryable = queryable
                                        .OrderBy($"{orderByKey}.Length {orderByItem.Direction}")
                                        .ThenBy($"{orderByKey} {orderByItem.Direction}");
                                }
                                else
                                {
                                    queryable = orderedQueryable
                                        .ThenBy($"{orderByKey}.Length {orderByItem.Direction}")
                                        .ThenBy($"{orderByKey} {orderByItem.Direction}");
                                }

                                break;
                            }
                        default:
                            throw new Exception("Method not supported.");
                    }
                }
            }
        }

        if (!orderByResult.Any())
        {
            var defaultOrderByList = new List<string>();

            foreach (var orderByItem in defaultOrderBy)
            {
                var memberNameSplit = orderByItem.orderByKey.Body.Print().Split(".").ToList();
                memberNameSplit.RemoveAt(0);
                var memberName = string.Join(".", memberNameSplit);

                var sortOrderString = orderByItem.sortDirection == ListSortDirection.Descending
                    ? "DESC"
                    : "ASC";

                orderByResult.Add(new OrderByCollectionRequestModel { Key = memberName, Direction = sortOrderString });

                defaultOrderByList.Add($"{memberName} {sortOrderString}");
            }

            if (defaultOrderByList.Count > 0)
            {
                var qOrderBy = string.Join(", ", defaultOrderByList);
                queryable = queryable.OrderBy(qOrderBy);
            }
        }

        return orderByResult;
    }

    protected static CollectionProcessingOptions<TEntity, TViewModel> CreateCollectionProcessingOptions<TEntity, TViewModel>(
        Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions)
        where TEntity : class
        where TViewModel : class
    {
        var builder = new CollectionProcessingOptionsBuilder<TEntity, TViewModel>();
        configureOptions.Invoke(builder);
        return builder.Build();
    }

    private static bool IsFilterAllowed<TEntity, TViewModel>(
        FilterCollectionRequestModel filter,
        IEnumerable<PropertyInfo> filterProperties,
        CollectionProcessingOptions<TEntity, TViewModel> options)
        where TEntity : class
        where TViewModel : class
    {
        var isAllowed = TryGetConfiguredFilterPath(options.FilterableSelectors, filter.Key, out _)
                        || options.UseFilterableAttributes && IsFilterKeyAllowed(filter, filterProperties);

        if (!isAllowed)
        {
            return false;
        }

        return (filter.Ands ?? []).All(x => IsFilterAllowed(x, filterProperties, options))
               && (filter.Ors ?? []).All(x => IsFilterAllowed(x, filterProperties, options));
    }

    private static bool IsFilterKeyAllowed(FilterCollectionRequestModel filter, IEnumerable<PropertyInfo> filterProperties)
    {
        if (string.IsNullOrWhiteSpace(filter.Key) || string.IsNullOrWhiteSpace(filter.Operator))
        {
            return false;
        }

        var key = filter.Key.Split(".")[0]
            .Replace("{any}", "", StringComparison.InvariantCultureIgnoreCase)
            .Replace("{!any}", "", StringComparison.InvariantCultureIgnoreCase)
            .Replace("{not any}", "", StringComparison.InvariantCultureIgnoreCase)
            .Replace("{all}", "", StringComparison.InvariantCultureIgnoreCase)
            .Replace("{!all}", "", StringComparison.InvariantCultureIgnoreCase)
            .Replace("{not all}", "", StringComparison.InvariantCultureIgnoreCase);

        return filterProperties.Any(x => x.Name.Equals(key, (StringComparison)3));
    }

    private static FilterCollectionRequestModel MapFilterKeys<TEntity>(
        FilterCollectionRequestModel filter,
        IReadOnlyDictionary<string, Expression<Func<TEntity, object?>>> filterableSelectors)
        where TEntity : class
    {
        return new FilterCollectionRequestModel
        {
            Key = TryGetConfiguredFilterPath(filterableSelectors, filter.Key, out var memberPath) ? memberPath : filter.Key,
            Operator = filter.Operator,
            Value = filter.Value,
            Ands = filter.Ands?.Select(x => MapFilterKeys(x, filterableSelectors)).ToList(),
            Ors = filter.Ors?.Select(x => MapFilterKeys(x, filterableSelectors)).ToList()
        };
    }

    private static bool IsOrderByAllowed<TViewModel>(
        string key,
        IEnumerable<PropertyInfo> orderByProperties,
        bool useOrderableAttributes)
        where TViewModel : class
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (!useOrderableAttributes)
        {
            return false;
        }

        var concatParts = key.Split("+").Select(x => x.Trim()).ToArray();
        foreach (var concatPart in concatParts)
        {
            var orderByKeys = concatPart
                .Split(".")
                .Select(x => x
                    .Replace("{first:asc}", "")
                    .Replace("{first:desc}", "")
                    .Replace("{last:asc}", "")
                    .Replace("{last:desc}", ""));

            PropertyInfo? orderByField = null;
            var objOrderByProperties = orderByProperties;

            foreach (var orderByKey in orderByKeys)
            {
                orderByField = objOrderByProperties.FirstOrDefault(x => x.Name.ToLower().Equals(orderByKey.ToLower()));

                if (orderByField == null)
                {
                    break;
                }

                if (!orderByField.PropertyType.IsClass)
                {
                    break;
                }

                var propertyType = orderByField.PropertyType;
                if (propertyType.IsGenericType && typeof(IEnumerable).IsAssignableFrom(propertyType))
                {
                    propertyType = propertyType.GetGenericArguments()[0];
                }

                objOrderByProperties = propertyType
                    .GetProperties()
                    .Where(prop => prop.IsDefined(typeof(OrderableAttribute), false));
            }

            if (orderByField == null)
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildOrderByKey(string key)
    {
        return string.Join(".", key
            .ToLower()
            .Trim()
            .Split(".")
            .Select(x => x.Trim())
            .Select(x => x
                .Replace("{first:asc}" + $"{x.Replace("{first:asc}", "")}",
                    $"OrderBy(sub => sub.{x.Replace("{first:asc}", "")}).First()")
                .Replace("{first:desc}" + $"{x.Replace("{first:desc}", "")}",
                    $"OrderByDescending(sub => sub.{x.Replace("{first:desc}", "")}).Last()")
                .Replace("{last:asc}" + $"{x.Replace("{last:asc}", "")}",
                    $"OrderBy(sub => sub.{x.Replace("{last:asc}", "")}).First()")
                .Replace("{last:desc}" + $"{x.Replace("{last:desc}", "")}",
                    $"OrderByDescending(sub => sub.{x.Replace("{last:desc}", "")}).Last()")));
    }

    private static void AddAttributeSearchExpressions<TEntity>(
        Type type,
        ParameterExpression parameter,
        ref Expression<Func<TEntity, bool>>? searchExpression,
        string qSearch,
        List<string>? prefixes = null)
        where TEntity : class
    {
        var searchProperties = type
            .GetProperties()
            .Where(prop => prop.IsDefined(typeof(SearchableAttribute), false));

        prefixes ??= [];

        foreach (var searchProperty in searchProperties)
        {
            if (searchProperty.PropertyType == typeof(string)
                || searchProperty.PropertyType == typeof(decimal)
                || searchProperty.PropertyType == typeof(int)
                || searchProperty.PropertyType == typeof(double))
            {
                var memberName = $"{(prefixes.Any() ? string.Join(".", prefixes) + "." : "")}{searchProperty.Name}";
                Expression member = parameter;

                foreach (var memberNamePart in memberName.Split('.'))
                {
                    member = Expression.PropertyOrField(member, memberNamePart);
                }

                AddSearchExpression(parameter, member, ref searchExpression, qSearch);
                continue;
            }

            if (!searchProperty.PropertyType.IsClass)
            {
                continue;
            }

            var subPrefixes = new List<string>(prefixes.ToArray()) { searchProperty.Name };

            if (subPrefixes.Count > 2)
            {
                continue;
            }

            var innerType = searchProperty.PropertyType;

            if (typeof(IEnumerable).IsAssignableFrom(searchProperty.PropertyType) &&
                searchProperty.PropertyType.IsGenericType)
            {
                if (innerType.IsGenericType &&
                    innerType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    innerType = innerType.GetGenericArguments()[0];
                }
                else
                {
                    var enumType = innerType.GetInterfaces()
                        .Where(t => t.IsGenericType &&
                                    t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                        .Select(t => t.GenericTypeArguments[0]).FirstOrDefault();
                    innerType = enumType ?? innerType;
                }

                continue;
            }

            AddAttributeSearchExpressions<TEntity>(innerType, parameter, ref searchExpression, qSearch, subPrefixes);
        }
    }

    private static void AddSearchExpression<TEntity>(
        ParameterExpression parameter,
        Expression member,
        ref Expression<Func<TEntity, bool>>? searchExpression,
        string qSearch)
    {
        member = StripConvert(member);

        if (member.Type != typeof(string))
        {
            member = Expression.Call(member, member.Type.GetMethod("ToString", Type.EmptyTypes)!);
        }

        var closure = new SearchClosure($"%{qSearch}%");
        var memberAccess = Expression.Property(Expression.Constant(closure), closure.GetType().GetProperty(nameof(SearchClosure.Value))!);

        Expression body = Expression.Call(
            typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like),
                new[] { typeof(DbFunctions), typeof(string), typeof(string) })!,
            Expression.Constant(EF.Functions),
            member,
            memberAccess);

        var subSearchExpression = Expression.Lambda<Func<TEntity, bool>>(body, parameter);

        searchExpression = searchExpression == null
            ? subSearchExpression
            : Expression.Lambda<Func<TEntity, bool>>(
                Expression.Or(searchExpression.Body, subSearchExpression.Body),
                searchExpression.Parameters);
    }

    private static void AddConfiguredSearchExpression<TEntity>(
        ParameterExpression parameter,
        Expression<Func<TEntity, object?>> selector,
        ref Expression<Func<TEntity, bool>>? searchExpression,
        string qSearch)
    {
        var body = BuildConfiguredSearchBody(
            StripConvert(selector.Body),
            selector.Parameters[0],
            parameter,
            qSearch);

        var subSearchExpression = Expression.Lambda<Func<TEntity, bool>>(body, parameter);

        searchExpression = searchExpression == null
            ? subSearchExpression
            : Expression.Lambda<Func<TEntity, bool>>(
                Expression.Or(searchExpression.Body, subSearchExpression.Body),
                searchExpression.Parameters);
    }

    private static Expression BuildConfiguredSearchBody(
        Expression expression,
        ParameterExpression sourceParameter,
        ParameterExpression targetParameter,
        string qSearch)
    {
        expression = StripConvert(expression);

        if (expression is MethodCallExpression methodCallExpression &&
            methodCallExpression.Method.Name == nameof(Enumerable.Select) &&
            methodCallExpression.Arguments.Count == 2)
        {
            var collection = ReplaceParameter(
                StripConvert(methodCallExpression.Arguments[0]),
                sourceParameter,
                targetParameter);

            var lambda = GetLambda(methodCallExpression.Arguments[1]);
            var itemParameter = lambda.Parameters[0];
            var itemBody = BuildConfiguredSearchBody(
                StripConvert(lambda.Body),
                itemParameter,
                itemParameter,
                qSearch);

            var itemType = GetEnumerableItemType(collection.Type);
            var predicateType = typeof(Func<,>).MakeGenericType(itemType, typeof(bool));
            var predicate = Expression.Lambda(predicateType, itemBody, itemParameter);
            var anyMethod = typeof(Enumerable)
                .GetMethods(BindingFlags.Static | BindingFlags.Public)
                .First(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
                .MakeGenericMethod(itemType);

            return Expression.Call(anyMethod, collection, predicate);
        }

        var member = ReplaceParameter(expression, sourceParameter, targetParameter);
        return BuildLikeExpression(member, qSearch);
    }

    private static Expression BuildLikeExpression(Expression member, string qSearch)
    {
        member = StripConvert(member);

        if (member.Type != typeof(string))
        {
            member = Expression.Call(member, member.Type.GetMethod("ToString", Type.EmptyTypes)!);
        }

        var closure = new SearchClosure($"%{qSearch}%");
        var memberAccess = Expression.Property(Expression.Constant(closure), closure.GetType().GetProperty(nameof(SearchClosure.Value))!);

        return Expression.Call(
            typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like),
                new[] { typeof(DbFunctions), typeof(string), typeof(string) })!,
            Expression.Constant(EF.Functions),
            member,
            memberAccess);
    }

    private static LambdaExpression GetLambda(Expression expression)
    {
        expression = StripConvert(expression);

        if (expression is UnaryExpression unaryExpression && unaryExpression.NodeType == ExpressionType.Quote)
        {
            expression = unaryExpression.Operand;
        }

        return expression as LambdaExpression
               ?? throw new ArgumentException("Collection projection methods must use a lambda selector.");
    }

    private static Type GetEnumerableItemType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType()!;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return type.GetGenericArguments()[0];
        }

        var enumerableType = type.GetInterfaces()
            .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumerableType?.GetGenericArguments()[0]
               ?? throw new ArgumentException($"Type '{type.Name}' is not an enumerable type.");
    }

    private static bool TryGetConfiguredFilterPath<TEntity>(
        IReadOnlyDictionary<string, Expression<Func<TEntity, object?>>> selectors,
        string key,
        out string memberPath)
        where TEntity : class
    {
        memberPath = string.Empty;

        if (!selectors.TryGetValue(key.Trim(), out var selector))
        {
            return false;
        }

        memberPath = CollectionProcessingExpressionResolver.GetFilterKey(selector);
        return true;
    }

    private static bool TryGetConfiguredOrderPath<TEntity>(
        IReadOnlyDictionary<string, Expression<Func<TEntity, object?>>> selectors,
        string key,
        out string memberPath)
        where TEntity : class
    {
        memberPath = string.Empty;

        if (!selectors.TryGetValue(key.Trim(), out var selector))
        {
            return false;
        }

        memberPath = CollectionProcessingExpressionResolver.GetOrderKey(selector);
        return true;
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

    private static Expression ReplaceParameter(Expression expression, ParameterExpression source, ParameterExpression target)
    {
        return new ParameterReplaceVisitor(source, target).Visit(expression)!;
    }

    private sealed class ParameterReplaceVisitor : ExpressionVisitor
    {
        private readonly ParameterExpression _source;
        private readonly ParameterExpression _target;

        public ParameterReplaceVisitor(ParameterExpression source, ParameterExpression target)
        {
            _source = source;
            _target = target;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == _source ? _target : base.VisitParameter(node);
        }
    }
}
