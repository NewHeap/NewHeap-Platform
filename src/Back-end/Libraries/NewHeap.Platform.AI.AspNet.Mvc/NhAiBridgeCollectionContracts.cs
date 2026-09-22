using System.Text.Json;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>The canonical collection contract associated with one bridged MVC action.</summary>
public sealed record NhAiBridgeCollectionContract(
    Type ItemType,
    NhAiBridgeQueryDescription Query);

/// <summary>
/// Recognizes a collection action and describes its item fields. Implementations may also encode
/// a noncanonical query wire contract while retaining the bridge's schema, authorization and
/// execution path.
/// </summary>
public interface INhAiBridgeCollectionContractProvider
{
    bool TryDescribe(
        NhAiBridgeActionInfo action,
        out NhAiBridgeCollectionContract contract);

    /// <summary>
    /// Encodes a custom collection query. Return <see langword="false"/> to use the canonical
    /// NewHeap <c>page</c>, <c>itemsPerPage</c>, <c>search</c>, <c>orderBy</c> and <c>filter</c>
    /// wire contract.
    /// </summary>
    bool TryEncodeQuery(
        NhAiBridgeActionInfo action,
        JsonElement input,
        NhAiBridgeHttpRequest request)
    {
        return false;
    }

    /// <summary>
    /// Marks an encoded single-page request as count-only for the gateway <c>countOnly</c> query,
    /// for example by adding a <c>countOnly=true</c> query value that the API supports. Return
    /// <see langword="false"/> to keep the default request for one item on the first page; the
    /// gateway then reads <c>totalCount</c> from that response.
    /// </summary>
    bool TryEncodeCountQuery(
        NhAiBridgeActionInfo action,
        NhAiBridgeHttpRequest request)
    {
        return false;
    }
}

/// <summary>
/// Recognizes canonical NewHeap collection request/result models and derives their query
/// capabilities from <c>Filterable</c>, <c>Orderable</c> and <c>Searchable</c> attributes.
/// </summary>
public sealed class NhAiNewHeapCollectionContractProvider : INhAiBridgeCollectionContractProvider
{
    public bool TryDescribe(
        NhAiBridgeActionInfo action,
        out NhAiBridgeCollectionContract contract)
    {
        ArgumentNullException.ThrowIfNull(action);
        var hasCanonicalRequest = action.Parameters.Any(parameter =>
            typeof(IBaseCollectionRequestModel).IsAssignableFrom(parameter.ParameterType));
        var hasCanonicalResult = NhCollectionContractMetadata.TryGetItemType(
            action.ResponseType,
            out var itemType);

        if (!hasCanonicalRequest && !hasCanonicalResult)
        {
            contract = null!;
            return false;
        }
        if (!hasCanonicalResult)
        {
            throw new InvalidOperationException(
                $"AI bridge collection action '{action.ControllerName}.{action.ActionName}' uses a canonical NewHeap collection request but its item type cannot be determined from the documented response. Add explicit CollectionResultModel<T> or SimpleCollectionResultModel<T> success-response metadata.");
        }

        var collection = NhCollectionContractMetadata.Describe(itemType);
        contract = new NhAiBridgeCollectionContract(
            itemType,
            new NhAiBridgeQueryDescription
            {
                FilterFields = collection.FilterFields.Select(field => new NhAiBridgeFilterField(
                    field.Name,
                    TypeName(field.FieldType),
                    field.Operators,
                    field.EnumValues)).ToArray(),
                Searchable = collection.SearchFields.Count > 0,
                OrderFields = collection.OrderFields.Select(field => field.Name).ToArray(),
                ResultFields = collection.ResultFields.Select(field => new NhAiBridgeResultField(
                    field.Name,
                    TypeName(field.FieldType))).ToArray()
            });
        return true;
    }

    private static string TypeName(NhCollectionFieldType fieldType)
    {
        return fieldType switch
        {
            NhCollectionFieldType.String => "string",
            NhCollectionFieldType.Boolean => "boolean",
            NhCollectionFieldType.Integer => "integer",
            NhCollectionFieldType.Number => "number",
            NhCollectionFieldType.DateTime => "date-time",
            NhCollectionFieldType.Enum => "enum",
            _ => "object"
        };
    }
}
