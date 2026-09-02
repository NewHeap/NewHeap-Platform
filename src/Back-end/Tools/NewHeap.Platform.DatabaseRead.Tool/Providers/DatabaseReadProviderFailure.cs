namespace NewHeap.Platform.DatabaseRead;

internal sealed record DatabaseReadProviderFailure(
    string Provider,
    string Classification,
    string? ProviderCode,
    bool Transient,
    string Message);
