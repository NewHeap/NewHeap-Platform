using NewHeap.Platform.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Utilities;
using System.Collections;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Linq.Dynamic.Core;
using NewHeap.Platform.Common.Extensions;
using NewHeap.Platform.Common.Exceptions;
using NewHeap.Platform.Common.Models.Options;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace NewHeap.Platform.Common.Services;
internal record SearchClosure(string Value);
public interface ICollectionProcessingService
{
    Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null, CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class;

    Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null, bool asNoTracking = true, CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class;

    Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
         where TEntity : class
         where TViewModel : class;

    Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null, bool asNoTracking = true, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions, Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null, bool asNoTracking = true, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null, CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class;

    Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null, bool asNoTracking = true, CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class;

    Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null, bool asNoTracking = true, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, Action<CollectionProcessingOptionsBuilder<TEntity, TViewModel>> configureOptions, Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null, bool asNoTracking = true, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<(IQueryable<TEntity> queryable, long totalCount, List<FilterCollectionRequestModel> filterResult, List<OrderByCollectionRequestModel> orderByResult)> ProcessQueryable<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class;
    Task<(IQueryable<TEntity> queryable, long totalCount, List<FilterCollectionRequestModel> filterResult, List<OrderByCollectionRequestModel> orderByResult)> ProcessQueryable<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    Task<(IQueryable<TEntity> queryable, long totalCount, List<FilterCollectionRequestModel> filterResult, List<OrderByCollectionRequestModel> orderByResult)> ProcessQueryable<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, CollectionProcessingOptions<TEntity, TViewModel> options, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    List<FilterCollectionRequestModel> ProcessFilter<TEntity, TViewModel>(ref IQueryable<TEntity> queryable, List<FilterCollectionRequestModel>? filterCollection)
        where TEntity : class
        where TViewModel : class;

    List<OrderByCollectionRequestModel> ProcessOrderBy<TEntity, TViewModel>(ref IQueryable<TEntity> queryable, List<OrderByCollectionRequestModel>? orderByCollection, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class;

    List<OrderByCollectionRequestModel> ProcessOrderBy<TEntity, TViewModel>(ref IQueryable<TEntity> queryable, List<OrderByCollectionRequestModel>? orderByCollection)
        where TEntity : class
        where TViewModel : class;
    void ProcessSearch<TEntity, TViewModel>(ref IQueryable<TEntity> queryable, string? qSearch)
        where TEntity : class
        where TViewModel : class;

    void ProcessSearch<TEntity, TViewModel>(ref IQueryable<TEntity> queryable, string? qSearch, CollectionProcessingOptions<TEntity, TViewModel> options)
        where TEntity : class
        where TViewModel : class;

    int GetDefaultMaxItemsPerPage();
    int GetDefaultItemsPerPage();
}

public partial class CollectionProcessingService : ICollectionProcessingService
{
    private static readonly string[] CollectionFilterTokens = ["{any}", "{!any}", "{not any}", "{all}", "{!all}", "{not all}"];

    protected readonly IMapper _mapper;
    private readonly NewHeapCommonSettings _settings;

    public CollectionProcessingService(
        IMapper mapper
        )
    {
        ArgumentNullException.ThrowIfNull(mapper);

        _mapper = mapper;
        _settings = new NewHeapCommonSettings();
    }

    public CollectionProcessingService(
        IMapper mapper,
        IOptions<NewHeapCommonSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        ArgumentNullException.ThrowIfNull(settings);

        ValidateSettings(settings.Value);

        _mapper = mapper;
        _settings = settings.Value;
    }

    public virtual int GetDefaultMaxItemsPerPage()
    {
        return _settings.CollectionProcessingDefaultMaxItemsPerPage;
    }

    public virtual int GetDefaultItemsPerPage()
    {
        return _settings.CollectionProcessingDefaultItemsPerPage;
    }

    private static void ValidateSettings(NewHeapCommonSettings settings)
    {
        if (settings.CollectionProcessingDefaultItemsPerPage < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.CollectionProcessingDefaultItemsPerPage,
                "CollectionProcessingDefaultItemsPerPage must be at least one.");
        }

        if (settings.CollectionProcessingDefaultMaxItemsPerPage < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.CollectionProcessingDefaultMaxItemsPerPage,
                "CollectionProcessingDefaultMaxItemsPerPage must be at least one.");
        }

        if (settings.CollectionProcessingDefaultItemsPerPage > settings.CollectionProcessingDefaultMaxItemsPerPage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.CollectionProcessingDefaultItemsPerPage,
                "CollectionProcessingDefaultItemsPerPage cannot exceed CollectionProcessingDefaultMaxItemsPerPage.");
        }

        if (settings.CollectionProcessingDeadlockMaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                settings.CollectionProcessingDeadlockMaxAttempts,
                "CollectionProcessingDeadlockMaxAttempts must be at least one.");
        }
    }

    public void ProcessSearch<TEntity, TViewModel>(
        ref IQueryable<TEntity> queryable,
        string? qSearch
    )
        where TEntity : class
        where TViewModel : class
    {
        if (!string.IsNullOrWhiteSpace(qSearch))
        {
            var _queryable = queryable;
            Expression<Func<TEntity, bool>>? searchExpression = null;
            var parameter = Expression.Parameter(typeof(TEntity), "x");

            void processSearch(Type type, List<string>? prefixes = null)
            {
                var searchProperties = type
                        .GetProperties()
                        .Where(prop => prop.IsDefined(typeof(SearchableAttribute), false))
                    ;

                if (prefixes == null)
                {
                    prefixes = new List<string>();
                }

                if (searchProperties.Any())
                {
                    foreach (var searchProperty in searchProperties)
                    {
                        if (searchProperty.PropertyType == typeof(string)
                            || searchProperty.PropertyType == typeof(decimal)
                            || searchProperty.PropertyType == typeof(int)
                            || searchProperty.PropertyType == typeof(double)
                           )
                        {
                            var memberName =
                                $"{(prefixes.Any() ? string.Join(".", prefixes) + "." : "")}{searchProperty.Name}";

                            Expression member = parameter;

                            foreach (var memberNamePart in memberName.Split('.'))
                            {
                                member = Expression.PropertyOrField(member, memberNamePart);
                            }

                            if (searchProperty.PropertyType != typeof(string))
                            {
                                member = Expression.Call(member, typeof(object).GetMethod("ToString")!);
                            }

                            var closure = new SearchClosure($"%{qSearch}%");
                            var memberAccess = Expression.Property(Expression.Constant(closure),
                                closure.GetType().GetProperty("Value")!);

                            Expression body = Expression.Call(
                                typeof(DbFunctionsExtensions).GetMethod(nameof(DbFunctionsExtensions.Like),
                                    new[] { typeof(DbFunctions), typeof(string), typeof(string) })!,
                                Expression.Constant(EF.Functions),
                                member,
                                memberAccess
                            );

                            var dbg = body.ToString();
                            var subSearchExpression = Expression.Lambda<Func<TEntity, bool>>(body, parameter);

                            if (searchExpression == null)
                            {
                                searchExpression = subSearchExpression;
                            }
                            else
                            {
                                searchExpression = Expression.Lambda<Func<TEntity, bool>>(
                                    Expression.Or(searchExpression.Body, subSearchExpression.Body),
                                    searchExpression.Parameters);
                            }

                            continue;
                        }

                        if (searchProperty.PropertyType.IsClass)
                        {
                            var subPrefixes = new List<string>(prefixes.ToArray());
                            subPrefixes.Add(searchProperty.Name);

                            if (subPrefixes.Count > 2)
                            {
                                //TODO: until we implement per controller search definitions, we only allow one depth
                                continue;
                            }

                            var innerType = searchProperty.PropertyType;

                            if (typeof(IEnumerable).IsAssignableFrom(searchProperty.PropertyType) &&
                                searchProperty.PropertyType.IsGenericType)
                            {
                                // type is IEnumerable<T>;
                                if (innerType.IsGenericType &&
                                    innerType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                                {
                                    innerType = innerType.GetGenericArguments()[0];
                                }
                                else
                                {
                                    // type implements/extends IEnumerable<T>;
                                    var enumType = innerType.GetInterfaces()
                                        .Where(t => t.IsGenericType &&
                                                    t.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                                        .Select(t => t.GenericTypeArguments[0]).FirstOrDefault();
                                    innerType = enumType ?? innerType;
                                }

                                //NOT supported (yet).
                                continue;
                            }

                            processSearch(innerType, subPrefixes);
                        }
                    }
                }
                else
                {
                    var isDebug =
                        Assembly.GetEntryAssembly()?
                            .GetCustomAttribute<DebuggableAttribute>()?
                            .DebuggingFlags.HasFlag(
                                DebuggableAttribute.DebuggingModes.DisableOptimizations)
                        ?? false;

                    if (isDebug)
                    {
                        throw new InvalidOperationException("Cannot search when no searchable properties are defined");
                    }
                }
            }

            processSearch(typeof(TViewModel));

            if (null != searchExpression)
            {
                queryable = _queryable.Where(searchExpression);
            }
        }
    }

    public List<FilterCollectionRequestModel> ProcessFilter<TEntity, TViewModel>(
        ref IQueryable<TEntity> queryable,
        List<FilterCollectionRequestModel>? filterCollection
    )
        where TEntity : class
        where TViewModel : class
    {
        var filterResult = new List<FilterCollectionRequestModel>();

        if (filterCollection != null && filterCollection.Any())
        {
            var filterProperties = typeof(TViewModel)
                    .GetProperties()
                    .Where(prop => prop.IsDefined(typeof(FilterableAttribute), false))
                ;

            if (filterProperties.Any())
            {
                var parameterIndex = new StrongBox<int>();
                foreach (var filter in filterCollection)
                {
                    var filterLambda = GetFilterLambdaCore<TEntity>(filter, filterProperties, parameterIndex);
                    if (filterLambda != null)
                    {
                        queryable = queryable.Where(filterLambda);
                    }

                    filterResult.Add(filter);
                }
            }
        }

        return filterResult;
    }

    protected bool IsFilterValid(FilterCollectionRequestModel filter, IEnumerable<PropertyInfo> filterProperties, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(filter.Key) || string.IsNullOrWhiteSpace(filter.Operator))
        {
            return false;
        }

        if (!NhCollectionContractMetadata.IsSupportedFilterOperator(filter.Operator))
        {
            error = $"Invalid operator '{filter.Operator}'";
            return false;
        }

        if (!IsFilterPathAllowed(filter.Key, filterProperties))
        {
            error = $"Invalid filter field '{filter.Key}'";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks every dotted segment of a client filter key against the view-model type graph.
    /// The first segment must be one of <paramref name="filterProperties"/>; every later segment
    /// must be a <see cref="FilterableAttribute"/> property of the previous segment's type, or of
    /// its element type when that segment is a collection. A key that reaches a member the view
    /// model does not mark filterable is rejected, even when the entity has that member.
    /// </summary>
    private static bool IsFilterPathAllowed(string key, IEnumerable<PropertyInfo> filterProperties)
    {
        var allowedProperties = filterProperties;

        foreach (var segment in key.Split('.'))
        {
            var memberName = RemoveCollectionFilterToken(segment);
            var property = allowedProperties.FirstOrDefault(x => x.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase));

            if (property == null)
            {
                return false;
            }

            var nextType = GetFilterCollectionItemType(property.PropertyType) ?? property.PropertyType;
            allowedProperties = nextType
                .GetProperties()
                .Where(prop => prop.IsDefined(typeof(FilterableAttribute), false))
                .ToArray();
        }

        return true;
    }

    private static string RemoveCollectionFilterToken(string segment)
    {
        foreach (var token in CollectionFilterTokens)
        {
            if (segment.EndsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                return segment[..^token.Length];
            }
        }

        return segment;
    }

    /// <summary>
    /// Returns the element type when a filter path step is a collection, so that validation and
    /// predicate building agree on where a key continues inside the collection items.
    /// </summary>
    private static Type? GetFilterCollectionItemType(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return type.GetGenericArguments()[0];
        }

        return type.GetInterfaces()
            .Where(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(x => x.GetGenericArguments()[0])
            .FirstOrDefault();
    }

    public static string? GetExpressionConstantString(object? value)
    { 
        if(value == null)
        {
            return null;
        } else if(value is DateTime || value is DateTime?)
        {
            return ((DateTime)value).ToString(Constants.DateTimeOffset.StringFormat);
        } else if(value is DateTimeOffset || value is DateTimeOffset?)
        {
            return ((DateTimeOffset)value).ToString(Constants.DateTimeOffset.StringFormat);
        }
        else
        {
            return value.ToString();
        }
    }

    protected static UnaryExpression? GetExpressionConstant(Type type, object? value)
    {
        var memberTypeConverter = TypeDescriptor.GetConverter(type);
        Expression<Func<object>> closure = () =>
            (value == null
                ? null
                : memberTypeConverter.ConvertFrom((value == null
                    ? null
                    : GetExpressionConstantString(value))!))!;

        return Expression.Convert(closure.Body, type);
    }

    protected Expression<Func<T, bool>>? GetFilterLambda<T>(FilterCollectionRequestModel filter,
        IEnumerable<PropertyInfo> filterProperties, ParameterExpression? parameter = null, bool skipValidation = false)
    {
        return GetFilterLambdaCore<T>(filter, filterProperties,
            new StrongBox<int>(parameter == null ? 0 : 1), parameter, skipValidation);
    }

    // skipValidation skips the check of this node's own key and operator. skipNestedValidation also
    // skips the check of its Ands and Ors nodes; only a caller that validated the complete filter
    // tree passes it.
    private Expression<Func<T, bool>>? GetFilterLambdaCore<T>(FilterCollectionRequestModel filter,
        IEnumerable<PropertyInfo> filterProperties, StrongBox<int> parameterIndex,
        ParameterExpression? parameter = null, bool skipValidation = false, bool skipNestedValidation = false)
    {
        if (!skipValidation && !IsFilterValid(filter, filterProperties, out var err))
        {
            throw new InvalidFilterCollectionResultException(err ?? "Invalid filter found");
        }

        // Number new parameters within this filter collection; EF includes their names in query-cache hashes.
        parameter ??= Expression.Parameter(typeof(T), "x" + (++parameterIndex.Value).ToString(CultureInfo.InvariantCulture));

        Expression member = parameter;
        var didCollectionHit = false;
        Expression? body = null;
        var collectionMethodName = "any";

        var keyParts = filter.Key.Split('.').ToList();
        for (var i = 0; i < keyParts.Count; i++)
        {
            // A collection step continues inside its items with the quantifier of that step's token.
            var collectionItemType = i == 0
                ? null
                : GetFilterCollectionItemType(member.Type);

            if (collectionItemType != null)
            {
                var remainderParts = keyParts.Skip(i);

                var parameter2 = Expression.Parameter(collectionItemType, "x" + (++parameterIndex.Value).ToString(CultureInfo.InvariantCulture));

                var filter2 = new FilterCollectionRequestModel
                {
                    Key = string.Join(".", remainderParts),
                    Operator = filter.Operator,
                    Value = filter.Value
                };

                dynamic? lambda2 = typeof(CollectionProcessingService)
                    .GetMethod(nameof(GetFilterLambdaCore), BindingFlags.Instance | BindingFlags.NonPublic)!
                    .MakeGenericMethod(collectionItemType)
                    .Invoke(this, new object[] { filter2, filterProperties, parameterIndex, parameter2, true, true });

                if (collectionMethodName == "any" || collectionMethodName == "!any")
                {
                    var anyMethod = typeof(Enumerable)
                        .GetMethods(BindingFlags.Static | BindingFlags.Public)
                        .First(m => m.Name == "Any" && m.GetParameters().Count() == 2);
                    var anyMethodConstructed = anyMethod.MakeGenericMethod(collectionItemType);

                    body = Expression.Call(
                        anyMethodConstructed,
                        member,
                        lambda2
                    );

                    if (collectionMethodName == "!any")
                    {
                        body = Expression.Not(body);
                    }
                }
                else if (collectionMethodName == "all" || collectionMethodName == "!all")
                {
                    var allMethod = typeof(Enumerable)
                        .GetMethods(BindingFlags.Static | BindingFlags.Public)
                        .First(m => m.Name == "All" && m.GetParameters().Count() == 2);
                    var allMethodConstructed = allMethod.MakeGenericMethod(collectionItemType);

                    body = Expression.Call(
                        allMethodConstructed,
                        member,
                        lambda2
                    );

                    if (collectionMethodName == "!all")
                    {
                        body = Expression.Not(body);
                    }
                }
                else
                {
                    throw new Exception("Collection method name not found.");
                }

                didCollectionHit = true;
                break;
            }

            var keyPart = keyParts[i];

            if (keyPart.ToLower().Contains("{any}"))
            {
                collectionMethodName = "any";
                keyPart = keyPart.Replace("{any}", "", StringComparison.OrdinalIgnoreCase);
            }
            else if (keyPart.ToLower().Contains("{!any}") || keyPart.ToLower().Contains("{not any}"))
            {
                collectionMethodName = "!any";
                keyPart = keyPart.Replace("{!any}", "", StringComparison.OrdinalIgnoreCase);
                keyPart = keyPart.Replace("{not any}", "", StringComparison.OrdinalIgnoreCase);
            }
            else if (keyPart.ToLower().Contains("{all}"))
            {
                collectionMethodName = "all";
                keyPart = keyPart.Replace("{all}", "", StringComparison.OrdinalIgnoreCase);
            }
            else if (keyPart.ToLower().Contains("{!all}") || keyPart.ToLower().Contains("{not all}"))
            {
                collectionMethodName = "!all";
                keyPart = keyPart.Replace("{!all}", "", StringComparison.OrdinalIgnoreCase);
                keyPart = keyPart.Replace("{not all}", "", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                collectionMethodName = "any";
            }

            member = Expression.PropertyOrField(member, keyPart);
        }

        if (!didCollectionHit)
        {
            switch (filter.Operator?.Trim().ToUpper())
            {
                case "IS":
                case "==":
                    {
                        var constant = GetExpressionConstant(member.Type, filter.Value)!;
                        body = Expression.Equal(member, constant);
                        break;
                    }
                case "IS NOT":
                case "!=":
                    {
                        var constant = GetExpressionConstant(member.Type, filter.Value)!;
                        body = Expression.NotEqual(member, constant);
                        break;
                    }
                case ">":
                    {
                        var constant = GetExpressionConstant(member.Type, filter.Value)!;
                        body = Expression.GreaterThan(member, constant);
                        break;
                    }
                case ">=":
                    {
                        var constant = GetExpressionConstant(member.Type, filter.Value)!;
                        body = Expression.GreaterThanOrEqual(member, constant);
                        break;
                    }
                case "<":
                    {
                        var constant = GetExpressionConstant(member.Type, filter.Value)!;
                        body = Expression.LessThan(member, constant);
                        break;
                    }
                case "<=":
                    {
                        var constant = GetExpressionConstant(member.Type, filter.Value)!;
                        body = Expression.LessThanOrEqual(member, constant);
                        break;
                    }
                case "IN":
                    {
                        var valueSplit = filter.Value is string
                            ? ((string)filter.Value).Split(',')
                            : ((IEnumerable)filter.Value!).Cast<object>().Select(x => x?.ToString()).ToArray();

                        if (!valueSplit.Any())
                        {
                            return null;
                        }

                        var constant = GetExpressionConstant(member.Type, valueSplit[0]!.Trim())!;
                        body = Expression.Equal(member, constant);

                        if (valueSplit.Length > 1)
                        {
                            for (var i = 1; i < valueSplit.Length; i++)
                            {
                                var value = valueSplit[i]!.Trim();
                                var constant2 = GetExpressionConstant(member.Type, value)!;
                                var body2 = Expression.Equal(member, constant2);
                                body = Expression.OrElse(body, body2);
                            }
                        }

                        break;
                    }
                case "NOT IN":
                    {
                        var valueSplit = filter.Value is string
                            ? ((string)filter.Value).Split(',')
                            : ((IEnumerable)filter.Value!).Cast<object>().Select(x => x?.ToString()).ToArray();

                        if (!valueSplit.Any())
                        {
                            return null;
                        }

                        var constant = GetExpressionConstant(member.Type, valueSplit[0]!.Trim())!;
                        body = Expression.NotEqual(member, constant);

                        if (valueSplit.Length > 1)
                        {
                            for (var i = 1; i < valueSplit.Length; i++)
                            {
                                var value = valueSplit[i]!.Trim();
                                var constant2 = GetExpressionConstant(member.Type, value)!;
                                var body2 = Expression.NotEqual(member, constant2);
                                body = Expression.AndAlso(body, body2);
                            }
                        }

                        break;
                    }
                case "LIKE":
                    {
                        var constant = GetExpressionConstant(member.Type, filter.Value)!;

                        body = Expression.Call(
                            typeof(DbFunctionsExtensions),
                            nameof(DbFunctionsExtensions.Like),
                            Type.EmptyTypes,
                            Expression.Property(null, typeof(EF), nameof(EF.Functions)),
                            member,
                            constant
                        );
                        break;
                    }
            }
        }

        if (filter.Ands != null)
        {
            foreach (var andFilter in filter.Ands)
            {
                var andFilterLambda = GetFilterLambdaCore<T>(andFilter, filterProperties, parameterIndex, parameter,
                    skipValidation: skipNestedValidation, skipNestedValidation: skipNestedValidation)!;
                body = Expression.AndAlso(body!, andFilterLambda.Body);
            }
        }

        if (filter.Ors != null)
        {
            foreach (var andFilter in filter.Ors)
            {
                var andFilterLambda = GetFilterLambdaCore<T>(andFilter, filterProperties, parameterIndex, parameter,
                    skipValidation: skipNestedValidation, skipNestedValidation: skipNestedValidation);
                body = Expression.OrElse(body!, andFilterLambda!.Body);
            }
        }

        return Expression.Lambda<Func<T, bool>>(body!, parameter);
    }

    protected List<OrderByCollectionRequestModel> _ProcessOrderBy<TEntity, TViewModel>(
        ref IQueryable<TEntity> queryable,
        List<OrderByCollectionRequestModel>? orderByCollection,
        List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy
    )
        where TEntity : class
        where TViewModel : class
    {
        var orderByResult = new List<OrderByCollectionRequestModel>();

        if (orderByCollection != null && orderByCollection.Any())
        {
            var orderByProperties = typeof(TViewModel)
                    .GetProperties()
                    .Where(prop => prop.IsDefined(typeof(OrderableAttribute), false))
                ;

            if (orderByProperties.Any())
            {
                foreach (var orderBy in orderByCollection)
                {
                    orderBy.Direction = (orderBy.Direction ?? "ASC").ToUpper();

                    if (string.IsNullOrWhiteSpace(orderBy.Key))
                    {
                        continue;
                    }

                    //Reslove
                    var orderByFields = new List<PropertyInfo>();
                    var concatParts = orderBy.Key.Split("+").Select(x => x.Trim());
                    foreach (var concatPart in concatParts)
                    {
                        var orderByKeys = concatPart
                            .Split(".")
                            .Select(x => x
                                .Replace("{first:asc}", "")
                                .Replace("{first:desc}", "")
                                .Replace("{last:asc}", "")
                                .Replace("{last:desc}", "")
                            );

                        PropertyInfo? orderByField = null;
                        var objOrderByProperties = typeof(TViewModel)
                                .GetProperties()
                                .Where(prop => prop.IsDefined(typeof(OrderableAttribute), false))
                            ;

                        foreach (var orderByKey in orderByKeys)
                        {
                            orderByField =
                                objOrderByProperties.FirstOrDefault(x => x.Name.ToLower().Equals(orderByKey.ToLower()));

                            if (null == orderByField)
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
                                    .Where(prop => prop.IsDefined(typeof(OrderableAttribute), false))
                                ;
                        }

                        if (null == orderByField)
                        {
                            continue;
                        }

                        orderByFields.Add(orderByField);
                    }

                    if (orderByFields.Count != concatParts.Count())
                    {
                        continue;
                    }

                    if (!orderBy.Direction.Equals("ASC") && !orderBy.Direction.Equals("DESC"))
                    {
                        orderBy.Direction = "ASC";
                    }

                    orderByResult.Add(orderBy);
                }

                if (orderByResult.Any())
                {
                    var count = 0;
                    foreach (var orderByItem in orderByResult)
                    {
                        count++;

                        var orderByKey = string.Join(".", orderByItem
                                ?.Key
                                ?.ToLower()
                                ?.Trim()
                                ?.Split(".")
                                .Select(x => x.Trim())
                                .Select(x => x
                                    .Replace("{first:asc}" + $"{x.Replace("{first:asc}", "")}",
                                        $"OrderBy(sub => sub.{x.Replace("{first:asc}", "")}).First()")
                                    .Replace("{first:desc}" + $"{x.Replace("{first:desc}", "")}",
                                        $"OrderByDescending(sub => sub.{x.Replace("{first:desc}", "")}).Last()")
                                    .Replace("{last:asc}" + $"{x.Replace("{last:asc}", "")}",
                                        $"OrderBy(sub => sub.{x.Replace("{last:asc}", "")}).First()")
                                    .Replace("{last:desc}" + $"{x.Replace("{last:desc}", "")}",
                                        $"OrderByDescending(sub => sub.{x.Replace("{last:desc}", "")}).Last()")
                                ) ?? []
                        );

                        switch (orderByItem?.Method)
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
                                        queryable = orderedQueryable.ThenBy($"{orderByKey} {orderByItem.Direction}");
                                    }

                                    break;
                                }
                            default:
                                throw new Exception("Method not supported.");
                        }
                    }
                }
            }
        }

        //Confirm order is present. We need a order by for Skip and Take results.
        if (!orderByResult.Any())
        {
            var defaultOrderByList = new List<string>();

            foreach (var orderByItem in defaultOrderBy)
            {
                //Todo: this seems hacky...
                var memberNameSplit = orderByItem.orderByKey.Body.Print().Split(".").ToList();
                memberNameSplit.RemoveAt(0);
                var memberName = string.Join(".", memberNameSplit);

                var sortOrderString = orderByItem.sortDirection == ListSortDirection.Descending
                    ? "DESC"
                    : "ASC";

                orderByResult.Add(new OrderByCollectionRequestModel { Key = memberName, Direction = sortOrderString });

                defaultOrderByList.Add($"{memberName} {sortOrderString}");
            }

            var qOrderBy = string.Join(", ", defaultOrderByList);
            queryable = queryable.OrderBy(qOrderBy);
        }

        return orderByResult;
    }

    public List<OrderByCollectionRequestModel> ProcessOrderBy<TEntity, TViewModel>(
        ref IQueryable<TEntity> queryable,
        List<OrderByCollectionRequestModel>? orderByCollection,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy
    )
        where TEntity : class
        where TViewModel : class
    {
        return _ProcessOrderBy<TEntity, TViewModel>(ref queryable, orderByCollection, defaultOrderBy.ToList());
    }

    public List<OrderByCollectionRequestModel> ProcessOrderBy<TEntity, TViewModel>(
        ref IQueryable<TEntity> queryable,
        List<OrderByCollectionRequestModel>? orderByCollection
    )
        where TEntity : class
        where TViewModel : class
    {
        return _ProcessOrderBy<TEntity, TViewModel>(ref queryable, orderByCollection, new List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)>());
    }


    protected async
        Task<(IQueryable<TEntity> queryable, long totalCount, List<FilterCollectionRequestModel> filterResult,
            List<OrderByCollectionRequestModel> orderByResult)> _ProcessQueryable<TEntity, TViewModel>(
            ICollectionRequestModel requestModel,
            IQueryable<TEntity> queryable,
            List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy,
            CancellationToken cancellationToken = default
        )
        where TEntity : class
        where TViewModel : class
    {
        ProcessSearch<TEntity, TViewModel>(ref queryable, requestModel.Search?.Trim());
        var filterResult = ProcessFilter<TEntity, TViewModel>(ref queryable, requestModel.Filter);
        var orderByResult = _ProcessOrderBy<TEntity, TViewModel>(ref queryable, requestModel.OrderBy, defaultOrderBy);

        var totalCount = await ExecuteDatabaseCommandAsync(
            token => queryable.GetType().GetInterfaces().Contains(typeof(IAsyncEnumerable<TEntity>))
                ? queryable.LongCountAsync(token)
                : Task.FromResult(queryable.LongCount()),
            cancellationToken);

        queryable = queryable
            .PageSkipTake(requestModel)
            .AsQueryable()
        ;

        return (queryable, totalCount, filterResult, orderByResult);
    }

    public virtual
        Task<(IQueryable<TEntity> queryable, long totalCount, List<FilterCollectionRequestModel> filterResult,
            List<OrderByCollectionRequestModel> orderByResult)> ProcessQueryable<TEntity, TViewModel>(
            ICollectionRequestModel requestModel,
            IQueryable<TEntity> queryable,
            CancellationToken cancellationToken = default,
            params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy
        )
        where TEntity : class
        where TViewModel : class
    {
        return _ProcessQueryable<TEntity, TViewModel>(
            requestModel,
            queryable,
            [.. defaultOrderBy],
            cancellationToken
        );
    }

    public virtual
    Task<(IQueryable<TEntity> queryable, long totalCount, List<FilterCollectionRequestModel> filterResult,
        List<OrderByCollectionRequestModel> orderByResult)> ProcessQueryable<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        CancellationToken cancellationToken = default
    )
        where TEntity : class
        where TViewModel : class
    {
        return _ProcessQueryable<TEntity, TViewModel>(
            requestModel,
            queryable,
            new List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)>(),
            cancellationToken
        );
    }

    protected async Task<CollectionResultModel<TViewModel>> _GetCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default
        )
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

        var processedResult = await _ProcessQueryable<TEntity, TViewModel>(
            requestModel,
            queryable,
            defaultOrderBy,
            cancellationToken: cancellationToken
        );

        queryable = processedResult.queryable;

        if (resultQueryableFunc != null)
        {
            queryable = await resultQueryableFunc.Invoke(queryable, cancellationToken);
        }

        var dbItems = await ExecuteDatabaseCommandAsync(
            token => queryable.GetType().GetInterfaces().Contains(typeof(IAsyncEnumerable<TEntity>))
                ? queryable.ToListAsync(token)
                : Task.FromResult(queryable.ToList()),
            cancellationToken);

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

    public virtual Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return GetCollectionResultModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            resultQueryableFunc,
            asNoTracking: true,
            cancellationToken,
            defaultOrderBy
        );
    }

    public virtual Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(
       ICollectionRequestModel requestModel,
       IQueryable<TEntity> queryable,
       Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
       bool asNoTracking = true,
       CancellationToken cancellationToken = default,
       params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
       where TEntity : class
       where TViewModel : class
    {
        return _GetCollectionResultModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            [.. defaultOrderBy],
            resultQueryableFunc,
            asNoTracking,
            cancellationToken
        );
    }


    public virtual Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        CancellationToken cancellationToken = default
    )
        where TEntity : class
        where TViewModel : class
    {
        return GetCollectionResultModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            resultQueryableFunc,
            asNoTracking: true,
            cancellationToken
        );
    }

    public virtual Task<CollectionResultModel<TViewModel>> GetCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default
    )
        where TEntity : class
        where TViewModel : class
    {
        return _GetCollectionResultModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            new List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)>(),
            resultQueryableFunc,
            asNoTracking,
            cancellationToken
        );
    }


    protected async Task<SimpleCollectionResultModel<TViewModel>> _GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)> defaultOrderBy,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default
        )
        where TEntity : class
        where TViewModel : class
    {
        var resultModel = await _GetCollectionResultModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            defaultOrderBy,
            resultQueryableFunc,
            asNoTracking,
            cancellationToken
        );

        return new SimpleCollectionResultModel<TViewModel>
        {
            Page = requestModel.Page,
            ItemsPerPage = requestModel.ItemsPerPage,
            TotalCount = resultModel.TotalCount,
            ResultCount = resultModel.ResultCount,
            Items = resultModel.Items
        };
    }

    public virtual Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            resultQueryableFunc,
            asNoTracking: true,
            cancellationToken
        );
    }

    public virtual Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return _GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            [.. defaultOrderBy],
            resultQueryableFunc,
            asNoTracking,
            cancellationToken
        );
    }

    public virtual Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        CancellationToken cancellationToken = default
    )
        where TEntity : class
        where TViewModel : class
    {
        return GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            resultQueryableFunc,
            asNoTracking: true,
            cancellationToken
        );
    }

    public virtual Task<SimpleCollectionResultModel<TViewModel>> GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
        ICollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Func<IQueryable<TEntity>, CancellationToken, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        bool asNoTracking = true,
        CancellationToken cancellationToken = default
    )
        where TEntity : class
        where TViewModel : class
    {
        return _GetSimpleCollectionResultModelAsync<TEntity, TViewModel>(
            requestModel,
            queryable,
            new List<(Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)>(),
            resultQueryableFunc,
            asNoTracking,
            cancellationToken
        );
    }

    #region Helpers
    private static bool IsIEnumerable(Type type)
    {
        return type.IsGenericType
               && type.GetGenericTypeDefinition() == typeof(IEnumerable<>);
    }
    private static Type GetIEnumerableImpl(Type type)
    {
        // Get IEnumerable implementation. Either type is IEnumerable<T> for some T, 
        // or it implements IEnumerable<T> for some T. We need to find the interface.
        if (IsIEnumerable(type))
        {
            return type;
        }

        Type[] t = type.FindInterfaces((m, o) => IsIEnumerable(m), null);

        return t[0];
    }
    #endregion
}
