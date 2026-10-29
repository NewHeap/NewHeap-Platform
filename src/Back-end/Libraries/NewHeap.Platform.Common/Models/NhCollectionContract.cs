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
    Object = 6,

    /// <summary>A <see cref="DateOnly"/>, filtered and written as <c>yyyy-MM-dd</c>.</summary>
    Date = 7,

    /// <summary>A <see cref="TimeOnly"/>, filtered and written as <c>HH:mm:ss</c>.</summary>
    Time = 8
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
    IReadOnlyList<NhCollectionFieldDescriptor> ResultFields)
{
    /// <summary>
    /// The dotted camelCase paths of the item members that a non-empty search matches when the
    /// collection runtime searches by <see cref="SearchableAttribute"/>, such as <c>name</c> or
    /// <c>owner.email</c>: <c>[Searchable]</c> string, <see cref="int"/>, <see cref="decimal"/> and
    /// <see cref="double"/> properties, and those of <c>[Searchable]</c> nested objects up to two
    /// levels deep. Collections are never searched. Selectors added with
    /// <c>CollectionProcessingOptionsBuilder.WithSearchable</c> are not part of the item type and
    /// are not listed.
    /// </summary>
    public IReadOnlyList<string> SearchPaths { get; init; } = [];
}

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
            properties.Select(property => DescribeField(property, includeOperators: false)).ToArray())
        {
            SearchPaths = SelectSearchPaths(itemType)
        };
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

    /// <summary>
    /// Follows the attribute search of <c>ICollectionProcessingService.ProcessSearch</c>, with and
    /// without options: the same properties, value types, nesting depth and skipped collections, so
    /// the paths name exactly the members a search reads.
    /// </summary>
    private static IReadOnlyList<string> SelectSearchPaths(Type itemType)
    {
        var paths = new List<string>();
        AddSearchPaths(itemType, [], paths);
        return paths;
    }

    private static void AddSearchPaths(Type type, IReadOnlyList<string> prefixes, List<string> paths)
    {
        var searchProperties = type
            .GetProperties()
            .Where(property => property.IsDefined(typeof(SearchableAttribute), false));

        foreach (var property in searchProperties)
        {
            var path = prefixes.Append(property.Name).ToArray();
            if (property.PropertyType == typeof(string)
                || property.PropertyType == typeof(decimal)
                || property.PropertyType == typeof(int)
                || property.PropertyType == typeof(double))
            {
                paths.Add(string.Join(".", path.Select(JsonNamingPolicy.CamelCase.ConvertName)));
                continue;
            }

            if (!property.PropertyType.IsClass || path.Length > 2)
            {
                continue;
            }

            if (typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType)
                && property.PropertyType.IsGenericType)
            {
                continue;
            }

            AddSearchPaths(property.PropertyType, path, paths);
        }
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
        if (type == typeof(DateOnly))
        {
            return NhCollectionFieldType.Date;
        }
        if (type == typeof(TimeOnly))
        {
            return NhCollectionFieldType.Time;
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
