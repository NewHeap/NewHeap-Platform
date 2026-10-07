namespace NewHeap.Platform.Common.Attributes;

/// <summary>Opt-in scalar field exposed by an explicitly selected collection.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SelectableAttribute : Attribute
{
}

/// <summary>
/// Access required in the opt-in field-selection pipeline. This does not change legacy responses.
/// Roles are OR-ed; roles and a policy, when both supplied, must both succeed.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class FieldAccessAttribute : Attribute
{
    public string? Roles { get; set; }
    public string? Policy { get; set; }
}
