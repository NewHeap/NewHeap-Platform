namespace NewHeap.Platform.AI;

/// <summary>
/// Describes the data of the application that the acting person may read, so a model knows where to
/// look without searching first. The API bridge gateway implements one from its resources;
/// applications may add their own with <c>AddApplicationMapSource&lt;T&gt;()</c>. Entries are data
/// for the model, never instructions.
/// </summary>
public interface INhAiApplicationMapSource
{
    /// <summary>
    /// The entries the caller of <paramref name="context"/> may read, in the order the map lists them.
    /// </summary>
    ValueTask<IReadOnlyList<NhAiApplicationMapEntry>> GetEntriesAsync(
        NhAiInvocationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// One readable resource of the application.
/// </summary>
/// <param name="Resource">The dash-case name the tools use for it, such as <c>project-task</c>.</param>
/// <param name="Operations">How it can be read, such as <c>query</c> and <c>get</c>.</param>
public sealed record NhAiApplicationMapEntry(
    string Resource,
    string Title,
    string Summary,
    IReadOnlyList<string> Operations)
{
    /// <summary>
    /// The fields that refer to other resources.
    /// </summary>
    public IReadOnlyList<NhAiApplicationMapRelation> Relations { get; init; } = [];

    /// <summary>
    /// The tools that read the resource. A turn lists the entry only when it offers one of them; an
    /// entry without tools is listed in every turn of an agent that uses the map.
    /// </summary>
    public IReadOnlyList<string> ToolIds { get; init; } = [];
}

/// <summary>
/// A field of one resource that holds the id of another, such as <c>projectId</c> to <c>project</c>.
/// </summary>
public sealed record NhAiApplicationMapRelation(string Field, string Resource);
