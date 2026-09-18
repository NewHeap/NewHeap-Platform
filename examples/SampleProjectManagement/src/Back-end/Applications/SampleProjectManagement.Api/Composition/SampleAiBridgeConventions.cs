using System.Reflection;
using System.Text.Json;
using NewHeap.Platform.AI.AspNet.Mvc;
using NewHeap.Platform.Common.Attributes;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Describes the collection fields of the sample API for the bridge gateway from the
/// <c>[Filterable]</c>, <c>[Orderable]</c> and <c>[Searchable]</c> attributes of the returned view
/// model. The gateway rejects other filter and order keys before the HTTP call.
/// </summary>
public sealed class SampleAiBridgeConventions : NhAiMvcBridgeDefaultConventions
{
    /// <summary>The operators of the NewHeap collection contract.</summary>
    private static readonly string[] FilterOperators = ["==", "!=", ">", ">=", "<", "<=", "IS", "IS NOT", "IN", "NOT IN", "LIKE"];

    public override NhAiBridgeQueryDescription DescribeQuery(NhAiBridgeActionInfo action)
    {
        var description = base.DescribeQuery(action);
        var itemType = ItemType(action.ResponseType);
        if (itemType is null)
        {
            return description;
        }

        var properties = itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        return description with
        {
            FilterFields = properties
                .Where(property => property.IsDefined(typeof(FilterableAttribute), inherit: true))
                .Select(property => new NhAiBridgeFilterField(
                    Key(property),
                    TypeName(property.PropertyType),
                    FilterOperators,
                    EnumValues(property.PropertyType)))
                .ToArray(),
            Searchable = description.Searchable
                && properties.Any(property => property.IsDefined(typeof(SearchableAttribute), inherit: true)),
            OrderFields = properties
                .Where(property => property.IsDefined(typeof(OrderableAttribute), inherit: true))
                .Select(Key)
                .ToArray(),
            ResultFields = properties
                .Select(property => new NhAiBridgeResultField(Key(property), TypeName(property.PropertyType)))
                .ToArray()
        };
    }

    /// <summary>The item type of a collection result, or the response type itself for a detail action.</summary>
    private static Type? ItemType(Type? responseType)
    {
        if (responseType is null)
        {
            return null;
        }

        var items = responseType.GetProperty("Items")?.PropertyType;
        if (items is { IsGenericType: true } && items.GetGenericArguments().Length == 1)
        {
            return items.GetGenericArguments()[0];
        }
        return responseType.IsClass && responseType != typeof(string) ? responseType : null;
    }

    private static string Key(PropertyInfo property)
    {
        return JsonNamingPolicy.CamelCase.ConvertName(property.Name);
    }

    private static IReadOnlyList<string>? EnumValues(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsEnum ? Enum.GetNames(underlying) : null;
    }

    private static string TypeName(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying switch
        {
            _ when underlying == typeof(string) || underlying == typeof(Guid) => "string",
            _ when underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) => "date-time",
            _ when underlying == typeof(bool) => "boolean",
            _ when underlying.IsEnum => "enum",
            _ when underlying == typeof(int) || underlying == typeof(long) => "integer",
            _ when underlying == typeof(decimal) || underlying == typeof(double) || underlying == typeof(float) => "number",
            _ => "object"
        };
    }
}
