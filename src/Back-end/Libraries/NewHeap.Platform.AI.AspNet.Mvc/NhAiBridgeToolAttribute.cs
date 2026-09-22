namespace NewHeap.Platform.AI.AspNet.Mvc;

/// <summary>
/// Adjusts how the API bridge publishes one MVC action. Every setting may only make the tool
/// more restrictive than the bridge defaults; a less cautious value fails at startup.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class NhAiBridgeToolAttribute : Attribute
{
    private NhAiToolEffect? _effect;

    /// <summary>
    /// The model-facing description. Takes precedence over the XML <c>summary</c> and the
    /// conventions.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>Excludes the action from the bridge.</summary>
    public bool Exclude { get; set; }

    /// <summary>
    /// A stricter governance effect than the HTTP method implies, such as
    /// <see cref="NhAiToolEffect.ExternalSideEffect"/> for a POST that sends mail.
    /// </summary>
    public NhAiToolEffect Effect
    {
        get => _effect ?? NhAiToolEffect.ReadOnly;
        set => _effect = value;
    }

    /// <summary>Overrides the result-size limit in bytes; 0 keeps the bridge default.</summary>
    public int MaxResultBytes { get; set; }

    /// <summary>Overrides the execution timeout in seconds; 0 keeps the bridge default.</summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>
    /// Requires approval for a read. Only valid on reads: every other effect already requires approval.
    /// </summary>
    public bool RequireApproval { get; set; }

    internal NhAiToolEffect? EffectOverride => _effect;
}
