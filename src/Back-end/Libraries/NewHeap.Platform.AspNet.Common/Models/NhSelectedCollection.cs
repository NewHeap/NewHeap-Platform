using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Models;

/// <summary>A selectable field visible to the current user and resource context.</summary>
public sealed record NhSelectableField(string Name, string Type, bool Filterable, bool Orderable, bool Searchable);

/// <summary>Request-scoped discovery metadata. Never cache this across users or divisions.</summary>
public sealed record NhSelectableCollection(IReadOnlyList<NhSelectableField> Fields, IReadOnlyList<string> Formats);

/// <summary>Selection metadata is present even when the selected page contains no rows.</summary>
public sealed record NhCollectionSelection(IReadOnlyList<string> RequestedFields, IReadOnlyList<string> ReturnedFields, string Format);

/// <summary>Only explicitly requested, authorized fields occur in each item.</summary>
public sealed class NhSelectedCollectionResult : CollectionResultModel<Dictionary<string, object?>>
{
    public required NhCollectionSelection Selection { get; init; }
}
