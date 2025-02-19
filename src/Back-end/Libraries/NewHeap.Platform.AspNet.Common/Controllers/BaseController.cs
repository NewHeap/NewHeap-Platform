using AutoMapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Identity.Claims;
using NewHeap.Platform.Common.Models;
using Newtonsoft.Json;
using System.Collections;
using System.ComponentModel;
using System.Linq.Dynamic.Core;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Claims;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public abstract partial class BaseController<TController, TBaseEntity> : ControllerBase
    where TController : BaseController<TController, TBaseEntity>
    where TBaseEntity : class
{
    protected readonly IConfiguration _config;
    protected readonly IStringLocalizer<TController> _localizer;
    protected readonly ILogger<TController> _logger;
    protected readonly IMapper _mapper;
    protected readonly NhUserManager _userManager;

    public BaseController(
        IMapper mapper,
        ILogger<TController> logger,
        IConfiguration config,
        IStringLocalizer<TController> localizer,
        NhUserManager userManager
    )
    {
        _mapper = mapper;
        _logger = logger;
        _config = config;
        _localizer = localizer;
        _userManager = userManager;
    }

    protected Guid? UserId
    {
        get
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                if (Guid.TryParse(User?.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                {
                    return userId;
                }
            }

            return null;
        }
    }

    protected Guid? ActiveDivisionId => HttpContext.Request.GetActiveDivisionId();

    [NonAction]
    protected virtual IQueryable<T> ApplyDivisionFilter<T>(IQueryable<T> query, Expression<Func<T, bool>> expression)
    {
        if (!User.HasClaim(NhPlatformClaimTypes.Permission,
                Platform.Common.Constants.DivisionPermissionClaimValues.AccessAll))
        {
            query = query.Where(expression);
        }

        return query;
    }

    [NonAction]
    protected async Task<User?> GetUser()
    {
        var user = UserId.HasValue ? await _userManager.FindByIdWithIncludesAsync(UserId.Value) : null;

        return user;
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(IdentityResult identityResult)
    {
        var localizedErrors = new List<LocalizedString>();

        foreach (var error in identityResult.Errors)
        {
            localizedErrors.Add(_localizer[error.Description]);
        }

        return BadRequest(localizedErrors);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(LocalizedString error)
    {
        var response = new BadRequestHttpResponseModel(error);

        return BadRequest(response);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(IEnumerable<LocalizedString> errors)
    {
        var response = new BadRequestHttpResponseModel(errors);

        return BadRequest(response);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(string error)
    {
        var response = new BadRequestHttpResponseModel(error);

        return BadRequest(response);
    }

    [NonAction]
    protected BadRequestObjectResult BadRequest(IEnumerable<string> errors)
    {
        var response = new BadRequestHttpResponseModel(errors);

        return BadRequest(response);
    }

    [NonAction]
    protected void ProcessSearch<TEntity, TViewModel>(
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

    [NonAction]
    protected List<FilterRequestModel> ProcessFilter<TEntity, TViewModel>(
        ref IQueryable<TEntity> queryable,
        List<FilterRequestModel>? filterCollection
    )
        where TEntity : class
        where TViewModel : class
    {
        var filterResult = new List<FilterRequestModel>();

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

    [NonAction]
    protected async Task<IActionResult> CollectionResultAsync<TModel, TViewModel>(IQueryable<TModel> query,
        Func<IQueryable<TModel>, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        int? maxItemsPerPage = null,
        params (Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TModel : class where TViewModel : class
    {
        maxItemsPerPage ??= GetDefaultMaxItemsPerPage();
        var collectionRequestModel = GetCollectionRequestModel(maxItemsPerPage);

        var collectionResponse = await GetCollectionResponseModel<TModel, TViewModel>(
            collectionRequestModel,
            query,
            resultQueryableFunc,
            defaultOrderBy
        );

        return Ok(collectionResponse);
    }

    /// <summary>
    ///     Output a CSV file
    /// </summary>
    /// <typeparam name="TModel">Type to query</typeparam>
    /// <typeparam name="TRowModel">Type representing the csv rows</typeparam>
    /// <param name="query">Query object</param>
    /// <param name="convert">
    ///     Method to convert
    ///     <typeparam name="TModel"></typeparam>
    ///     to
    ///     <typeparam name="TRowModel"></typeparam>
    ///     .
    ///     When null the default mapper will be used.
    /// </param>
    /// <param name="resultQueryableFunc">Function for selecting
    ///     <typeparam name="TModel"></typeparam>
    ///     . Can be used to include extra data.
    /// </param>
    /// <param name="includeHeaders"></param>
    /// <param name="defaultOrderBy">Order by clauses</param>
    /// <param name="delimiter"></param>
    /// <returns></returns>
    protected async Task<IActionResult> Csv<TModel, TRowModel>(IQueryable<TModel> query,
        Func<IEnumerable<TModel>, IEnumerable<TRowModel>>? convert = null,
        Func<IQueryable<TModel>, Task<IQueryable<TModel>>>? resultQueryableFunc = null,
        char delimiter = ';',
        bool includeHeaders = false,
        params (Expression<Func<TModel, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TModel : class
        where TRowModel : class
    {
        var collectionRequestModel = GetCollectionRequestModel(int.MaxValue);
        collectionRequestModel.ItemsPerPage = int.MaxValue;

        query = query.AsNoTracking();

        var collectionResponseModel =
            await GetCollectionResponseModel<TModel, TModel>(collectionRequestModel, query, resultQueryableFunc,
                defaultOrderBy);

        IEnumerable<TRowModel>? rows = null;

        if (convert != null)
        {
            rows = convert(collectionResponseModel.Items);
        }
        else if (typeof(TRowModel) != typeof(TModel))
        {
            rows = _mapper.Map<IEnumerable<TRowModel>>(collectionResponseModel.Items);
        }
        else
        {
            rows = collectionResponseModel.Items.Select(x => (x as TRowModel)!).ToList();
        }

        var rowType = typeof(TRowModel);
        var properties = rowType.GetProperties();
        var fileStream = new MemoryStream(); // This is disposed by the File method call
        await fileStream.WriteAsync(Encoding.UTF8.GetPreamble()); //Set file encoding to UTF-8

        if (includeHeaders)
        {
            await fileStream.WriteAsync(Encoding.UTF8.GetBytes(string.Join(delimiter, properties.Select(x => x.Name)) +
                                                               Environment.NewLine));
        }

        foreach (var row in rows)
        {
            await fileStream.WriteAsync(Encoding.UTF8.GetBytes(
                    string.Join(delimiter, properties.Select(p => p.GetMethod!.Invoke(row, null))) + Environment.NewLine
                )
            );
        }

        fileStream.Seek(0,
            SeekOrigin.Begin); // Reset stream to the start or else we're not going to write much to the response

        return File(fileStream, "text/csv");
    }


    [NonAction]
    private bool IsFilterValid(FilterRequestModel filter, IEnumerable<PropertyInfo> filterProperties)
    {
        if (string.IsNullOrWhiteSpace(filter.Key) || string.IsNullOrWhiteSpace(filter.Operator))
        {
            return false;
        }

        var supportedOperators = new[] { "==", "!=", ">", ">=", "<", "<=", "IS", "IS NOT", "IN", "NOT IN", "LIKE" };

        if (!supportedOperators.Contains(filter.Operator))
        {
            return false;
        }

        var key = filter.Key.Split(".")[0]
                .Replace("{any}", "")
                .Replace("{!any}", "")
                .Replace("{not any}", "")
                .Replace("{all}", "")
                .Replace("{!all}", "")
                .Replace("{not all}", "")
            ;

        var filterField = filterProperties.FirstOrDefault(x => x.Name.Equals(key, (StringComparison)3));
        if (null == filterField)
        {
            return false;
        }

        return true;
    }

    [NonAction]
    private static Expression? JoinExpressions(IList<Expression> expressions,
        Func<Expression, Expression, Expression> join)
    {
        Expression? andExpression = null;

        foreach (var expression in expressions)
        {
            if (andExpression == null)
            {
                andExpression = expression;
            }
            else
            {
                andExpression = join(andExpression, expression);
            }
        }

        return andExpression;
    }

    [NonAction]
    private static Expression CreateMemberAccess(Expression target, string selector)
    {
        var expression = target;
        var selectorParts = selector.Split('.').ToList();
        for (var i = 0; i < selectorParts.Count; i++)
        {
            var selectorPart = selectorParts[i];
            expression = Expression.PropertyOrField(expression, selectorPart);
        }

        return expression;
    }

    [NonAction]
    private Expression<Func<T, bool>>? GetFilterLambda<T>(FilterRequestModel filter,
        IEnumerable<PropertyInfo> filterProperties, ParameterExpression? parameter = null, bool skipValidation = false)
    {
        if (!skipValidation && !IsFilterValid(filter, filterProperties))
        {
            throw new Exception("Invalid filter found");
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
                var expression2 = CreateMemberAccess(parameter2, string.Join('.', remainderParts));

                var filter2 = new FilterRequestModel
                {
                    Key = string.Join(".", remainderParts), Operator = filter.Operator, Value = filter.Value
                };

                dynamic? lambda2 = typeof(BaseController<TController, TBaseEntity>)
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
            switch (filter.Operator)
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

    [NonAction]
    protected List<OrderByRequestModel> ProcessOrderBy<TEntity, TViewModel>(
        ref IQueryable<TEntity> queryable,
        List<OrderByRequestModel>? orderByCollection,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy
    )
        where TEntity : class
        where TViewModel : class
    {
        var orderByResult = new List<OrderByRequestModel>();

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

                orderByResult.Add(new OrderByRequestModel { Key = memberName, Direction = sortOrderString });

                defaultOrderByList.Add($"{memberName} {sortOrderString}");
            }

            var qOrderBy = string.Join(", ", defaultOrderByList);
            queryable = queryable.OrderBy(qOrderBy);
        }

        return orderByResult;
    }

    [NonAction]
    protected virtual async
        Task<(IQueryable<TEntity> queryable, long totalCount, List<FilterRequestModel> filterResult,
            List<OrderByRequestModel> orderByResult)> ProcessQueryable<TEntity, TViewModel>(
            CollectionRequestModel requestModel,
            IQueryable<TEntity> queryable,
            params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy
        )
        where TEntity : class
        where TViewModel : class
    {
        ProcessSearch<TEntity, TViewModel>(ref queryable, requestModel.Search?.Trim());
        var filterResult = ProcessFilter<TEntity, TViewModel>(ref queryable, requestModel.Filter);
        var orderByResult = ProcessOrderBy<TEntity, TViewModel>(ref queryable, requestModel.OrderBy, defaultOrderBy);

        var totalCount = queryable.GetType().GetInterfaces().Contains(typeof(IAsyncEnumerable<TEntity>))
            ? await queryable.LongCountAsync()
            : queryable.LongCount();

        queryable = queryable
                .Skip((requestModel.Page - 1) * requestModel.ItemsPerPage)
                .Take(requestModel.ItemsPerPage)
            ;

        return (queryable, totalCount, filterResult, orderByResult);
    }

    protected virtual int GetDefaultMaxItemsPerPage()
    {
        return 1000;
    }


    [NonAction]
    protected virtual CollectionRequestModel GetCollectionRequestModel(int? maxItemsPerPage = null)
    {
        var request = HttpContext.Request;

        maxItemsPerPage ??= GetDefaultMaxItemsPerPage();
        var defaultItemsPerPage = 20;

        var qPage = request.Query["page"];
        var qItemsPerPage = request.Query["itemsPerPage"];
        string? qOrderBy = request.Query["orderBy"];
        string? qSearch = request.Query["search"];
        string? qFilter = request.Query["filter"];

        if (!int.TryParse(qPage.FirstOrDefault(), out var page) || page < 1)
        {
            page = 1;
        }

        int.TryParse(qItemsPerPage.FirstOrDefault(), out var itemsPerPage2);

        if (!int.TryParse(qItemsPerPage.FirstOrDefault(), out var itemsPerPage) || itemsPerPage > maxItemsPerPage)
        {
            itemsPerPage = defaultItemsPerPage;
        }

        qSearch = qSearch?.Trim();

        List<OrderByRequestModel> orderBy = new List<OrderByRequestModel>();
        var filter = new List<FilterRequestModel>();

        try
        {
            if (!string.IsNullOrWhiteSpace(qOrderBy))
            {
                orderBy = JsonConvert.DeserializeObject<List<OrderByRequestModel>>(qOrderBy)!;
            }

            if (!string.IsNullOrWhiteSpace(qFilter))
            {
                filter = JsonConvert.DeserializeObject<List<FilterRequestModel>>(qFilter);
            }
        }
        catch
        {
            //Ignore
        }

        return new CollectionRequestModel
        {
            Page = page,
            ItemsPerPage = itemsPerPage,
            Search = qSearch,
            OrderBy = orderBy ?? [],
            Filter = filter ?? []
        };
    }

    [NonAction]
    protected virtual async Task<IQueryable<TEntity>> GetCollectionResponseQuery<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        var processedResult = await ProcessQueryable<TEntity, TViewModel>(
            requestModel,
            queryable,
            defaultOrderBy
        );

        queryable = processedResult.queryable;

        return queryable;
    }

    /// <summary>
    /// </summary>
    /// <typeparam name="TEntity"></typeparam>
    /// <typeparam name="TViewModel"></typeparam>
    /// <param name="requestModel">RequestModel, <see cref="GetCollectionRequestModel" /></param>
    /// <param name="queryable">Collection to search</param>
    /// <param name="resultQueryableFunc">Function to execute on the result</param>
    /// <param name="defaultOrderBy">Order by properties</param>
    /// <returns></returns>
    [NonAction]
    protected virtual async Task<CollectionResponseModel<TViewModel>> GetCollectionResponseModel<TEntity, TViewModel>(
        CollectionRequestModel requestModel,
        IQueryable<TEntity> queryable,
        Func<IQueryable<TEntity>, Task<IQueryable<TEntity>>>? resultQueryableFunc = null,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        queryable = queryable.AsNoTracking();
        var processedResult = await ProcessQueryable<TEntity, TViewModel>(
            requestModel,
            queryable,
            defaultOrderBy
        );

        queryable = processedResult.queryable;

        if (resultQueryableFunc != null)
        {
            queryable = await resultQueryableFunc.Invoke(queryable);
        }

        var dbItems = queryable.GetType().GetInterfaces().Contains(typeof(IAsyncEnumerable<TEntity>))
            ? await queryable.ToListAsync()
            : queryable.ToList();

        var items = typeof(TViewModel).Equals(typeof(TEntity))
            ? (List<TViewModel>)(object)dbItems
            : _mapper.Map<List<TViewModel>>(dbItems);

        return new CollectionResponseModel<TViewModel>
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

    [NonAction]
    protected virtual async Task<CollectionResponseModel<TViewModel>> GetCollectionResponseModel<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        var requestModel = GetCollectionRequestModel();

        return await GetCollectionResponseModel<TEntity, TViewModel>(requestModel, queryable, null, defaultOrderBy);
    }

    [NonAction]
    protected virtual async Task<OkObjectResult> Ok<TEntity, TViewModel>(
        IQueryable<TEntity> queryable,
        params (Expression<Func<TEntity, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TViewModel : class
    {
        return Ok(await GetCollectionResponseModel<TEntity, TViewModel>(queryable, defaultOrderBy));
    }
}

internal record SearchClosure(string Value);