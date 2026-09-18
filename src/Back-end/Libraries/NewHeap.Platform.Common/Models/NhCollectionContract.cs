using System.Reflection;
using System.Text.Json;
using NewHeap.Platform.Common.Attributes;

namespace NewHeap.Platform.Common.Models;

/// <summary>The provider-neutral field types exposed by a NewHeap collection contract.</summary>
public enum NhCollectionFieldType
{
    String = 0,
    Boolean = 1,
    Integer = 2,
    Number = 3,
    DateTime = 4,
    Enum = 5,
    Object = 6
}

/// <summary>One field that participates in a NewHeap collection contract.</summary>
public sealed record NhCollectionFieldDescriptor(
    string Name,
    NhCollectionFieldType FieldType,
    IReadOnlyList<string> Operators,
    IReadOnlyList<string>? EnumValues = null);

/// <summary>
/// The filter, order, search and result metadata of one NewHeap collection item type.
/// </summary>
public sealed record NhCollectionContract(
    Type ItemType,
    IReadOnlyList<NhCollectionFieldDescriptor> FilterFields,
    IReadOnlyList<NhCollectionFieldDescriptor> OrderFields,
    IReadOnlyList<NhCollectionFieldDescriptor> SearchFields,
    IReadOnlyList<NhCollectionFieldDescriptor> ResultFields);

/// <summary>
/// Describes canonical NewHeap collection request and result contracts from the same attributes
/// and operator vocabulary used by <see cref="Services.ICollectionProcessingService"/>.
/// </summary>
public static class NhCollectionContractMetadata
{
    private static readonly string[] FilterOperators =
        ["==", "!=", ">", ">=", "<", "<=", "IS", "IS NOT", "IN", "NOT IN", "LIKE"];

    /// <summary>The filter operators supported by the NewHeap collection runtime.</summary>
    public static IReadOnlyList<string> SupportedFilterOperators => FilterOperators;

    /// <summary>Builds metadata for a collection item type.</summary>
    public static NhCollectionContract Describe(Type itemType)
    {
        ArgumentNullException.ThrowIfNull(itemType);
        var properties = itemType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0)
            .ToArray();

        return new NhCollectionContract(
            itemType,
            SelectFields<FilterableAttribute>(properties, includeOperators: true),
            SelectFields<OrderableAttribute>(properties, includeOperators: false),
            SelectFields<SearchableAttribute>(properties, includeOperators: false),
            properties.Select(property => DescribeField(property, includeOperators: false)).ToArray());
    }

    /// <summary>
    /// Resolves the item type of a canonical <see cref="CollectionResultModel{T}"/> or
    /// <see cref="SimpleCollectionResultModel{T}"/> result, including derived result types.
    /// </summary>
    public static bool TryGetItemType(Type? resultType, out Type itemType)
    {
        for (var candidate = resultType; candidate is not null && candidate != typeof(object); candidate = candidate.BaseType)
        {
            if (!candidate.IsGenericType)
            {
                continue;
            }

            var definition = candidate.GetGenericTypeDefinition();
            if (definition == typeof(CollectionResultModel<>)
                || definition == typeof(SimpleCollectionResultModel<>))
            {
                itemType = candidate.GetGenericArguments()[0];
                return true;
            }
        }

        itemType = null!;
        return false;
    }

    /// <summary>Returns whether the operator is accepted by the collection runtime.</summary>
    public static bool IsSupportedFilterOperator(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && FilterOperators.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<NhCollectionFieldDescriptor> SelectFields<TAttribute>(
        IEnumerable<PropertyInfo> properties,
        bool includeOperators)
        where TAttribute : Attribute
    {
        return properties
            .Where(property => property.IsDefined(typeof(TAttribute), inherit: true))
            .Select(property => DescribeField(property, includeOperators))
            .ToArray();
    }

    private static NhCollectionFieldDescriptor DescribeField(PropertyInfo property, bool includeOperators)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return new NhCollectionFieldDescriptor(
            JsonNamingPolicy.CamelCase.ConvertName(property.Name),
            Classify(type),
            includeOperators ? FilterOperators : [],
            type.IsEnum ? Enum.GetNames(type) : null);
    }

    private static NhCollectionFieldType Classify(Type type)
    {
        if (type == typeof(string) || type == typeof(Guid))
        {
            return NhCollectionFieldType.String;
        }
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return NhCollectionFieldType.DateTime;
        }
        if (type == typeof(bool))
        {
            return NhCollectionFieldType.Boolean;
        }
        if (type.IsEnum)
        {
            return NhCollectionFieldType.Enum;
        }
        if (type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong))
        {
            return NhCollectionFieldType.Integer;
        }
        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
        {
            return NhCollectionFieldType.Number;
        }
        return NhCollectionFieldType.Object;
    }
}
