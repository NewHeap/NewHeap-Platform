namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Configuration bound from <c>NewHeap:AI:Assistant</c>.
/// </summary>
public sealed class NhAssistantOptions
{
    public const string SectionName = "NewHeap:AI:Assistant";
    public const string DefaultAccessPolicy = "app.assistant.access";

    /// <summary>
    /// Kill switch. When false, <c>GET status</c> reports <c>enabled: false</c> without agents and
    /// every other assistant endpoint returns <c>404</c>. Defaults to false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Authorization policy every assistant endpoint requires unless
    /// <c>NhAssistantBuilder.UseAccessPolicy</c> sets one explicitly.
    /// </summary>
    public string AccessPolicy { get; set; } = DefaultAccessPolicy;
}
