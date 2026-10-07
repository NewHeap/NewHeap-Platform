using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Exceptions;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace NewHeap.Platform.AspNet.Common.Services;

/// <summary>
/// Explicit, scalar field selection. Controllers retain their existing path unless HasSelection is true.
/// Consumers own row scoping and provide a provider-translatable member-initializer projection.
/// </summary>
public sealed class NhFieldSelectionService(
    ICollectionProcessingService collections,
    IAuthorizationService authorization,
    IOptions<MvcNewtonsoftJsonOptions> jsonOptions)
{
    public const int MaxFields = 64;
    private const int MaxQueryNodes = 100;

    public static bool HasSelection(HttpRequest request)
    {
        return request.Query.ContainsKey("fields");
    }

    public async Task<NhSelectableCollection> DescribeAsync<TView>(
        ClaimsPrincipal user, object? resource = null, CancellationToken cancellationToken = default)
    {
        var fields = await GetAllowedFieldsAsync<TView>(user, resource, cancellationToken);
        return new NhSelectableCollection(fields.Select(field => new NhSelectableField(
            field.Name, FieldType(field.Property.PropertyType),
            field.Has<FilterableAttribute>(), field.Has<OrderableAttribute>(), field.Has<SearchableAttribute>())).ToArray(),
            ["json", "messagepack"]);
    }

    public async Task<TaskResult<NhSelectedCollectionResult>> GetCollectionAsync<TEntity, TView>(
        HttpContext context,
        ICollectionRequestModel request,
        IQueryable<TEntity> source,
        Expression<Func<TEntity, TView>> projection,
        object? resource = null,
        CancellationToken cancellationToken = default,
        params (Expression<Func<TView, object>> orderByKey, ListSortDirection sortDirection)[] defaultOrderBy)
        where TEntity : class
        where TView : class, new()
    {
        if (!HasSelection(context.Request))
        {
            return Invalid("fields-required", "The fields parameter is required for field selection.");
        }

        context.Response.Headers.CacheControl = "no-store";

        var rawFields = context.Request.Query["fields"];
        if (rawFields.ToString().Length > 4096)
        {
            return Invalid("invalid-fields", "The field selection is too large.");
        }

        var requested = rawFields.SelectMany(value => (value ?? "").Split(','))
            .Select(value => value.Trim()).ToArray();
        if (requested.Length == 0 || requested.Length > MaxFields || requested.Any(string.IsNullOrEmpty))
        {
            return Invalid("invalid-fields", "Select between 1 and 64 fields using comma-separated names.");
        }

        var formats = context.Request.Query["format"];
        var format = formats.Count == 0 ? "json" : formats.ToString().Trim().ToLowerInvariant();
        if (format is not ("json" or "messagepack"))
        {
            return Invalid("invalid-format", "The format must be json or messagepack.");
        }

        // The legacy HTTP binder tolerates malformed JSON. The opt-in path rejects it explicitly.
        foreach (var key in new[] { "filter", "orderBy" })
        {
            if (!context.Request.Query.TryGetValue(key, out var json))
            {
                continue;
            }
            try
            {
                using var reader = new JsonTextReader(new StringReader(json.ToString())) { MaxDepth = 32 };
                if (JToken.ReadFrom(reader) is not JArray array || reader.Read())
                {
                    return Invalid("invalid-query", "Collection filters and ordering must be JSON arrays.");
                }
                // Also validate values which the legacy binder would silently discard.
                if (key == "filter")
                {
                    _ = array.ToObject<List<FilterCollectionRequestModel>>();
                }
                else
                {
                    _ = array.ToObject<List<OrderByCollectionRequestModel>>();
                }
            }
            catch (JsonException)
            {
                return Invalid("invalid-query", "Collection filters and ordering must be valid JSON arrays.");
            }
        }

        var allowed = await GetAllowedFieldsAsync<TView>(context.User, resource, cancellationToken);
        var lookup = allowed.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        if (requested.Any(name => !lookup.ContainsKey(name)))
        {
            return Invalid("field-unavailable", "One or more requested fields are unavailable.");
        }
        var selected = requested.Select(name => lookup[name]).DistinctBy(field => field.Name).ToArray();

        var queryNodes = 0;
        if (request.OrderBy is null || !ValidateFilters(request.Filter, lookup, ref queryNodes)
            || request.OrderBy.Count > MaxFields
            || request.OrderBy.Any(order => order is null || string.IsNullOrWhiteSpace(order.Key)
                || string.IsNullOrWhiteSpace(order.Direction) || !lookup.TryGetValue(order.Key, out var field)
                || !field.Has<OrderableAttribute>()
                || order.Direction.ToUpperInvariant() is not ("ASC" or "DESC")
                || order.Method != OrderByMethod.Default))
        {
            return Invalid("query-field-unavailable", "The query contains an unavailable field or operation.");
        }
        if (!string.IsNullOrWhiteSpace(request.Search) && !allowed.Any(field => field.Has<SearchableAttribute>()))
        {
            return Invalid("search-unavailable", "Search is unavailable for this collection.");
        }

        if (projection.Body is not MemberInitExpression initializer
            || initializer.NewExpression.Arguments.Count != 0
            || initializer.Bindings.Any(binding => binding is not MemberAssignment))
        {
            throw new ArgumentException("Field selection requires a parameterless member-initializer projection.", nameof(projection));
        }
        var bindings = initializer.Bindings.ToDictionary(binding => binding.Member.Name);
        if (allowed.Any(field => !bindings.ContainsKey(field.Property.Name)))
        {
            throw new InvalidOperationException("Every selectable field must have an explicit projection binding.");
        }

        var row = Expression.Parameter(typeof(TView), "row");
        var selectedProjection = Expression.Lambda<Func<TView, TView>>(
            Expression.MemberInit(Expression.New(typeof(TView)), selected.Select(field =>
                Expression.Bind(field.Property, Expression.Property(row, field.Property)))), row);

        // Keep query fields until after filtering and paging. EF prunes unselected output bindings.
        try
        {
            var result = await collections.GetCollectionResultModelAsync<TView, TView>(
                request, source.Select(projection),
                options => ConfigureQuery(options, allowed),
                (query, _) => Task.FromResult(query.Select(selectedProjection)),
                asNoTracking: true, cancellationToken: cancellationToken,
                defaultOrderBy: defaultOrderBy);

            return TaskResult<NhSelectedCollectionResult>.Succeeded(new NhSelectedCollectionResult
            {
                Page = result.Page,
                ItemsPerPage = result.ItemsPerPage,
                TotalCount = result.TotalCount,
                ResultCount = result.ResultCount,
                Filter = result.Filter,
                OrderBy = result.OrderBy,
                Search = result.Search,
                Items = result.Items.Select(item => (Dictionary<string, object?>)new SelectedRow(selected.ToDictionary(
                    field => field.Name, field => field.Property.GetValue(item)))).ToList(),
                Selection = new NhCollectionSelection(requested, selected.Select(field => field.Name).ToArray(), format)
            });
        }
        catch (InvalidFilterCollectionResultException)
        {
            return Invalid("invalid-filter", "The filter value is invalid for this collection.");
        }
    }

    public IActionResult ToActionResult(TaskResult<NhSelectedCollectionResult> result)
    {
        if (!result.Success)
        {
            return new BadRequestObjectResult(result);
        }
        if (result.Data!.Selection.Format == "messagepack")
        {
            return new FileContentResult(
                NhSelectedCollectionMessagePack.Serialize(result.Data, jsonOptions.Value.SerializerSettings),
                "application/x-msgpack");
        }
        return new OkObjectResult(result.Data);
    }

    private async Task<List<Field>> GetAllowedFieldsAsync<TView>(
        ClaimsPrincipal user, object? resource, CancellationToken cancellationToken)
    {
        var settings = jsonOptions.Value.SerializerSettings;
        var resolver = settings.ContractResolver ?? new DefaultContractResolver();
        if (resolver.ResolveContract(typeof(TView)) is not JsonObjectContract contract)
        {
            throw new InvalidOperationException("A selected collection requires an object JSON contract.");
        }
        var result = new List<Field>();
        var policies = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var property in typeof(TView).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.IsDefined(typeof(SelectableAttribute), true))
            {
                continue;
            }
            var jsonProperty = contract.Properties.FirstOrDefault(candidate => candidate.UnderlyingName == property.Name);
            if (jsonProperty is null || jsonProperty.Ignored)
            {
                continue;
            }
            if (property.GetMethod?.IsPublic != true || property.SetMethod?.IsPublic != true
                || property.GetIndexParameters().Length != 0 || FieldType(property.PropertyType) == "unsupported"
                || jsonProperty.Converter is not null || jsonProperty.ShouldSerialize is not null)
            {
                throw new InvalidOperationException($"Selectable field {typeof(TView).Name}.{property.Name} requires a readable, writable scalar without a property converter or conditional serialization.");
            }

            var allowed = true;
            foreach (var access in property.GetCustomAttributes<FieldAccessAttribute>(true))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var roles = (access.Roles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (roles.Length == 0 && string.IsNullOrWhiteSpace(access.Policy))
                {
                    throw new InvalidOperationException("FieldAccess requires roles or a policy.");
                }
                if (roles.Length > 0 && !roles.Any(user.IsInRole))
                {
                    allowed = false;
                }
                if (!string.IsNullOrWhiteSpace(access.Policy))
                {
                    if (!policies.TryGetValue(access.Policy, out var granted))
                    {
                        granted = (await authorization.AuthorizeAsync(user, resource, access.Policy)).Succeeded;
                        policies.Add(access.Policy, granted);
                    }
                    allowed &= granted;
                }
            }
            if (allowed)
            {
                result.Add(new Field(property, jsonProperty.PropertyName!));
            }
        }
        return result;
    }

    private static void ConfigureQuery<TView>(CollectionProcessingOptionsBuilder<TView, TView> options, List<Field> fields)
        where TView : class
    {
        options.FilterableFromAttributes(false).OrderableFromAttributes(false).SearchableFromAttributes(false);
        foreach (var field in fields)
        {
            var parameter = Expression.Parameter(typeof(TView), "row");
            var selector = Expression.Lambda<Func<TView, object?>>(
                Expression.Convert(Expression.Property(parameter, field.Property), typeof(object)), parameter);
            if (field.Has<FilterableAttribute>())
            {
                options.WithFilterable(field.Name, selector);
            }
            if (field.Has<OrderableAttribute>())
            {
                options.WithOrderable(field.Name, selector);
            }
            if (field.Has<SearchableAttribute>())
            {
                options.WithSearchable(selector);
            }
        }
    }

    private static bool ValidateFilters(IEnumerable<FilterCollectionRequestModel>? filters,
        Dictionary<string, Field> fields, ref int count)
    {
        foreach (var filter in filters ?? [])
        {
            if (++count > MaxQueryNodes || filter is null || string.IsNullOrWhiteSpace(filter.Key)
                || !fields.TryGetValue(filter.Key, out var field)
                || !field.Has<FilterableAttribute>() || !NhCollectionContractMetadata.IsSupportedFilterOperator(filter.Operator)
                || !ValidateFilters(filter.Ands, fields, ref count) || !ValidateFilters(filter.Ors, fields, ref count))
            {
                return false;
            }
        }
        return true;
    }

    private static string FieldType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type == typeof(Guid))
        {
            return "string";
        }
        if (type == typeof(bool))
        {
            return "boolean";
        }
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly))
        {
            return "date";
        }
        if (type.IsEnum)
        {
            return "enum";
        }
        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)
            || type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
        {
            return "number";
        }
        return "unsupported";
    }

    private static TaskResult<NhSelectedCollectionResult> Invalid(string code, string message)
    {
        return TaskResult<NhSelectedCollectionResult>.Failed(code, message);
    }

    private sealed record Field(PropertyInfo Property, string Name)
    {
        public bool Has<TAttribute>() where TAttribute : Attribute
        {
            return Property.IsDefined(typeof(TAttribute), true);
        }
    }

    // Resolved JSON aliases must not undergo dictionary-key naming a second time.
    [JsonDictionary(NamingStrategyType = typeof(DefaultNamingStrategy))]
    private sealed class SelectedRow(Dictionary<string, object?> values) : Dictionary<string, object?>(values);
}
