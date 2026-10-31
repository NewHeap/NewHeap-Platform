using System.Text.Json;

namespace NewHeap.Platform.DatabaseRead;

/// <summary>
/// The first line of a session: what a caller detects the capability by, written before any
/// profile, configuration or connection is touched.
/// </summary>
internal sealed class DatabaseReadSessionReadyLine
{
    public required DatabaseReadSessionCapabilities Session { get; init; }
}

internal sealed class DatabaseReadSessionCapabilities
{
    public required int ProtocolVersion { get; init; }

    public required IReadOnlyList<string> Commands { get; init; }

    public required IReadOnlyList<string> SchemaOperations { get; init; }
}

/// <summary>
/// One line of a session's input. <see cref="Request"/> is exactly the body the one-shot command
/// reads; <see cref="Id"/> is opaque and comes back on the answer.
/// </summary>
internal sealed class DatabaseReadSessionRequest
{
    public string? Id { get; init; }

    public string? Command { get; init; }

    public JsonElement Request { get; init; }
}

/// <summary>
/// How long a session lives and how long it trusts a verification of the read-only principal.
/// </summary>
internal sealed record DatabaseReadSessionOptions(
    TimeSpan MaximumLifetime,
    TimeSpan MaximumIdleTime,
    TimeSpan VerificationInterval)
{
    public const int DefaultMaximumLifetimeSeconds = 3_600;
    public const int DefaultMaximumIdleSeconds = 300;
    public const int DefaultVerificationIntervalSeconds = 60;

    /// <summary>
    /// The ceiling of a session's lifetime and idle time, one day.
    /// </summary>
    public const int HardMaximumLifetimeSeconds = 86_400;

    /// <summary>
    /// The ceiling of the verification interval, one hour, so a principal that gained write access
    /// never stays trusted for long.
    /// </summary>
    public const int HardMaximumVerificationIntervalSeconds = 3_600;

    public static DatabaseReadSessionOptions Default { get; } = new(
        TimeSpan.FromSeconds(DefaultMaximumLifetimeSeconds),
        TimeSpan.FromSeconds(DefaultMaximumIdleSeconds),
        TimeSpan.FromSeconds(DefaultVerificationIntervalSeconds));
}
