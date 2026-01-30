using AutoMapper;
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
using System.Linq.Dynamic.Core;
using NewHeap.Platform.Common.Extensions;
using NewHeap.Platform.Common.Exceptions;

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

    Task<(IQueryable<TEntity> queryable, long totalCount, List<FilterCollectionRequestModel> filterResult, List<OrderByCollectionRequestModel> orderByResult)> ProcessQueryable<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, CancellationToken cancellationToken = default)
        where TEntity : class
        where TViewModel : class;
    Task<(IQueryable<TEntity> queryable, long totalCount, List<FilterCollectionRequestModel> filterResult, List<OrderByCollectionRequestModel> orderByResult)> ProcessQueryable<TEntity, TViewModel>(ICollectionRequestModel requestModel, IQueryable<TEntity> queryable, CancellationToken cancellationToken = default, params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
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

    int GetDefaultMaxItemsPerPage();
    int GetDefaultItemsPerPage();
}

public partial class CollectionProcessingService : ICollectionProcessingService
{
    protected readonly IMapper _mapper;

    public CollectionProcessingService(
        IMapper mapper
        )
    {
        _mapper = mapper;
    }
    public virtual int GetDefaultMaxItemsPerPage()
    {
        // TODO: Get this from the configuration / factory
        return 1000;
    }

    public virtual int GetDefaultItemsPerPage()
    {
        // TODO: Get this from the configuration / factory
        return 20;
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
                foreach (var filter in filterCollection)
                {
                    var filterLambda = GetFilterLambda<TEntity>(filter, filterProperties);
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

        var supportedOperators = new[] { "==", "!=", ">", ">=", "<", "<=", "IS", "IS NOT", "IN", "NOT IN", "LIKE" };

        if (!supportedOperators.Contains(filter.Operator?.Trim(),StringComparer.InvariantCultureIgnoreCase))
        {
            error = $"Invalid operator '{filter.Operator}'";
            return false;
        }

        var key = filter.Key.Split(".")[0]
                .Replace("{any}", "",StringComparison.InvariantCultureIgnoreCase)
                .Replace("{!any}", "",StringComparison.InvariantCultureIgnoreCase)
                .Replace("{not any}", "",StringComparison.InvariantCultureIgnoreCase)
                .Replace("{all}", "",StringComparison.InvariantCultureIgnoreCase)
                .Replace("{!all}", "",StringComparison.InvariantCultureIgnoreCase)
                .Replace("{not all}", "",StringComparison.InvariantCultureIgnoreCase)
            ;

        var filterField = filterProperties.FirstOrDefault(x => x.Name.Equals(key, (StringComparison)3));
        if (null == filterField)
        {
            error = $"Invalid filter field '{key}'";
            return false;
        }

        return true;
    }

    protected Expression<Func<T, bool>>? GetFilterLambda<T>(FilterCollectionRequestModel filter,
        IEnumerable<PropertyInfo> filterProperties, ParameterExpression? parameter = null, bool skipValidation = false)
    {
        if (!skipValidation && !IsFilterValid(filter, filterProperties, out var err))
        {
            if (err is not null)
            {
                throw new Exception(err);
            }
            else
            {
                throw new InvalidFilterCollectionResultException("Invalid filter found");
            }
        }

        var selector = Guid.NewGuid().ToString().Replace("-", string.Empty).Substring(0, 8);
        parameter = parameter ?? Expression.Parameter(typeof(T), selector);

        Expression member = parameter;
        var didCollectionHit = false;
        Expression? body = null;
        var collectionMethodName = "any";

        var keyParts = filter.Key.Split('.').ToList();
        for (var i = 0; i < keyParts.Count; i++)
        {
            var keyPart = keyParts[i];

            if (keyPart.ToLower().Contains("{any}"))
            {
                collectionMethodName = "any";
                keyPart = keyPart.Replace("{any}", "");
            }
            else if (keyPart.ToLower().Contains("{!any}") || keyPart.ToLower().Contains("{not any}"))
            {
                collectionMethodName = "!any";
                keyPart = keyPart.Replace("{!any}", "");
                keyPart = keyPart.Replace("{not any}", "");
            }
            else if (keyPart.ToLower().Contains("{all}"))
            {
                collectionMethodName = "all";
                keyPart = keyPart.Replace("{all}", "");
            }
            else if (keyPart.ToLower().Contains("{!all}") || keyPart.ToLower().Contains("{not all}"))
            {
                collectionMethodName = "!all";
                keyPart = keyPart.Replace("{!all}", "");
                keyPart = keyPart.Replace("{not all}", "");
            }

            var method = member.Type.GetMethods()
                .FirstOrDefault(m => m.Name == "GetEnumerator" && m.ReturnType.IsGenericType);
            if (method != null)
            {
                var remainderParts = keyParts.Skip(i);
                var collectionItemType = method.ReturnType.GetGenericArguments()[0];

                var selector2 = Guid.NewGuid().ToString().Replace("-", string.Empty).Substring(0, 8);
                var parameter2 = Expression.Parameter(collectionItemType, selector2);
                var expression2 = ExpressionUtils.CreateMemberAccess(parameter2, string.Join('.', remainderParts));

                var filter2 = new FilterCollectionRequestModel
                {
                    Key = string.Join(".", remainderParts),
                    Operator = filter.Operator,
                    Value = filter.Value
                };

                dynamic? lambda2 = typeof(CollectionProcessingService)
                    .GetMethod("GetFilterLambda", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .MakeGenericMethod(collectionItemType)
                    .Invoke(this, new object[] { filter2, filterProperties, parameter2, true });

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
                collectionMethodName = "any"; // Restore;
                break;
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
                        var memberTypeConverter = TypeDescriptor.GetConverter(member.Type);
                        Expression<Func<object>> closure = () =>
                            (filter.Value == null
                                ? null
                                : memberTypeConverter.ConvertFrom((filter.Value == null
                                    ? null
                                    : filter.Value.ToString())!))!;
                        var constant = Expression.Convert(closure.Body, member.Type);
                        body = Expression.Equal(member, constant);
                        break;
                    }
                case "IS NOT":
                case "!=":
                    {
                        var memberTypeConverter = TypeDescriptor.GetConverter(member.Type);
                        Expression<Func<object>> closure = () =>
                            (filter.Value == null
                                ? null
                                : memberTypeConverter.ConvertFrom((filter.Value == null
                                    ? null
                                    : filter.Value.ToString())!))!;
                        var constant = Expression.Convert(closure.Body, member.Type);
                        body = Expression.NotEqual(member, constant);
                        break;
                    }
                case ">":
                    {
                        var memberTypeConverter = TypeDescriptor.GetConverter(member.Type);
                        Expression<Func<object>> closure = () =>
                            (filter.Value == null
                                ? null
                                : memberTypeConverter.ConvertFrom((filter.Value == null
                                    ? null
                                    : filter.Value.ToString())!))!;
                        var constant = Expression.Convert(closure.Body, member.Type);
                        body = Expression.GreaterThan(member, constant);
                        break;
                    }
                case ">=":
                    {
                        var memberTypeConverter = TypeDescriptor.GetConverter(member.Type);
                        Expression<Func<object>> closure = () =>
                            (filter.Value == null
                                ? null
                                : memberTypeConverter.ConvertFrom((filter.Value == null
                                    ? null
                                    : filter.Value.ToString())!))!;
                        var constant = Expression.Convert(closure.Body, member.Type);
                        body = Expression.GreaterThanOrEqual(member, constant);
                        break;
                    }
                case "<":
                    {
                        var memberTypeConverter = TypeDescriptor.GetConverter(member.Type);
                        Expression<Func<object>> closure = () =>
                            (filter.Value == null
                                ? null
                                : memberTypeConverter.ConvertFrom((filter.Value == null
                                    ? null
                                    : filter.Value.ToString())!))!;
                        var constant = Expression.Convert(closure.Body, member.Type);
                        body = Expression.LessThan(member, constant);
                        break;
                    }
                case "<=":
                    {
                        var memberTypeConverter = TypeDescriptor.GetConverter(member.Type);
                        Expression<Func<object>> closure = () =>
                            (filter.Value == null
                                ? null
                                : memberTypeConverter.ConvertFrom((filter.Value == null
                                    ? null
                                    : filter.Value!.ToString())!))!;
                        var constant = Expression.Convert(closure.Body, member.Type);
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

                        var memberTypeConverter = TypeDescriptor.GetConverter(member.Type);
                        Expression<Func<object>> closure = () => memberTypeConverter.ConvertFrom(valueSplit[0]!.Trim())!;
                        var constant = Expression.Convert(closure.Body, member.Type);
                        body = Expression.Equal(member, constant);

                        if (valueSplit.Length > 1)
                        {
                            for (var i = 1; i < valueSplit.Length; i++)
                            {
                                var value = valueSplit[i]!.Trim();
                                Expression<Func<object>> closure2 = () => memberTypeConverter.ConvertFrom(value)!;
                                var constant2 = Expression.Convert(closure2.Body, member.Type);
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

                        var memberTypeConverter = TypeDescriptor.GetConverter(member.Type);
                        Expression<Func<object>> closure = () => memberTypeConverter.ConvertFrom(valueSplit[0]!.Trim())!;
                        var constant = Expression.Convert(closure.Body, member.Type);
                        body = Expression.NotEqual(member, constant);

                        if (valueSplit.Length > 1)
                        {
                            for (var i = 1; i < valueSplit.Length; i++)
                            {
                                var value = valueSplit[i]!.Trim();
                                Expression<Func<object>> closure2 = () => memberTypeConverter.ConvertFrom(value)!;
                                var constant2 = Expression.Convert(closure2.Body, member.Type);
                                var body2 = Expression.NotEqual(member, constant2);
                                body = Expression.AndAlso(body, body2);
                            }
                        }

                        break;
                    }
                case "LIKE":
                    {
                        var memberTypeConverter = TypeDescriptor.GetConverter(member.Type);
                        Expression<Func<object>> closure = () =>
                            (filter.Value == null
                                ? null
                                : memberTypeConverter.ConvertFrom((filter.Value == null
                                    ? null
                                    : filter.Value.ToString())!))!;
                        var constant = Expression.Convert(closure.Body, member.Type);

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
                var andFilterLambda = GetFilterLambda<T>(andFilter, filterProperties, parameter)!;
                body = Expression.AndAlso(body!, andFilterLambda.Body);
            }
        }

        if (filter.Ors != null)
        {
            foreach (var andFilter in filter.Ors)
            {
                var andFilterLambda = GetFilterLambda<T>(andFilter, filterProperties, parameter);
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

        var totalCount = queryable.GetType().GetInterfaces().Contains(typeof(IAsyncEnumerable<TEntity>))
            ? await queryable.LongCountAsync(cancellationToken)
            : queryable.LongCount();

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