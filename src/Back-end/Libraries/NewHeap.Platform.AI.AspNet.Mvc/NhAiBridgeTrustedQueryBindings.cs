namespace NewHeap.Platform.AI.AspNet.Mvc;

public enum NhAiBridgeTrustedQueryBindingKind
{
    QueryValue = 0,
    CollectionFilter = 1
}

/// <summary>
/// Maps one audited invocation-scope value to a query value or collection filter. The model
/// cannot supply the value; it is resolved from <see cref="NhAiInvocationContext.Scope"/>.
/// </summary>
public sealed record NhAiBridgeTrustedQueryBinding(
    string Name,
    string ScopeKey,
    NhAiBridgeTrustedQueryBindingKind Kind = NhAiBridgeTrustedQueryBindingKind.QueryValue,
    string Operator = "==");

/// <summary>Contributes trusted, actor- or scope-bound query values for one bridge action.</summary>
public interface INhAiBridgeTrustedQueryBindingProvider
{
    ValueTask<IReadOnlyList<NhAiBridgeTrustedQueryBinding>> GetBindingsAsync(
        NhAiBridgeActionInfo action,
        NhAiToolDescriptor descriptor,
        NhAiInvocationContext context,
        CancellationToken cancellationToken = default);
}
