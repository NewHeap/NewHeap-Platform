using System.Security.Cryptography;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Web Push settings bound from <c>NewHeap:AI:Assistant:Push</c>. Push notifications are available
/// when <see cref="PublicKey"/>, <see cref="PrivateKey"/> and <see cref="Subject"/> are set. Create a
/// key pair once with <see cref="NhAssistantWebPushKeys.Generate"/> and keep the private key in the
/// host's secret store.
/// </summary>
public sealed class NhAssistantPushOptions
{
    /// <summary>
    /// The default push services of Chrome, Edge, Firefox and Safari.
    /// </summary>
    public static IReadOnlyList<string> DefaultAllowedEndpointHosts { get; } =
    [
        "fcm.googleapis.com",
        "updates.push.services.mozilla.com",
        "*.notify.windows.com",
        "*.push.apple.com"
    ];

    /// <summary>
    /// VAPID application server public key: the uncompressed P-256 point (65 bytes), base64url.
    /// </summary>
    public string? PublicKey { get; set; }

    /// <summary>
    /// VAPID private key: the P-256 private scalar (32 bytes), base64url. Never expose it to clients.
    /// </summary>
    public string? PrivateKey { get; set; }

    /// <summary>
    /// Contact for push services, a <c>mailto:</c> address or an <c>https:</c> URL (RFC 8292).
    /// </summary>
    public string? Subject { get; set; }

    /// <summary>
    /// Host names or <c>*.</c> suffix patterns that subscription endpoints may use. Subscriptions for
    /// other hosts are refused, so the server never posts to an address a client chose. Defaults to
    /// <see cref="DefaultAllowedEndpointHosts"/>.
    /// </summary>
    public IList<string> AllowedEndpointHosts { get; set; } = [.. DefaultAllowedEndpointHosts];

    /// <summary>
    /// A finished or failed turn notifies only when it ran at least this long, so quick answers the
    /// user is still waiting for do not notify. An approval request always notifies the person who
    /// must decide. Defaults to 10 seconds.
    /// </summary>
    public TimeSpan MinimumTurnDuration { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Shows the conversation title in the notification. Turn it off when titles may be too sensitive
    /// for a lock screen. Notifications never contain messages or tool content. Defaults to true.
    /// </summary>
    public bool IncludeConversationTitle { get; set; } = true;

    /// <summary>
    /// How long a push service keeps an undelivered notification. Defaults to 4 hours.
    /// </summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromHours(4);

    /// <summary>
    /// Maximum browsers per actor; the least recently refreshed subscription is removed first.
    /// </summary>
    public int MaxSubscriptionsPerActor { get; set; } = 10;

    /// <summary>
    /// Maximum time for one delivery to a push service.
    /// </summary>
    public TimeSpan DeliveryTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// True when the VAPID keys and subject are set.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(Subject);

    /// <summary>
    /// Throws when configured values are invalid. Unconfigured push stays disabled without an error.
    /// </summary>
    internal void Validate()
    {
        if (MinimumTurnDuration < TimeSpan.Zero
            || TimeToLive <= TimeSpan.Zero
            || TimeToLive > TimeSpan.FromDays(28)
            || MaxSubscriptionsPerActor is < 1 or > 100
            || DeliveryTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Assistant push limits must be positive and bounded.");
        }
        if (!IsConfigured)
        {
            return;
        }
        if (!Subject!.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            && !(Uri.TryCreate(Subject, UriKind.Absolute, out var subject) && subject.Scheme == Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("The assistant push subject must be a mailto: address or an https URL.");
        }
        // Fails clearly at startup instead of at the first notification.
        using var _ = NhAssistantWebPushKeys.CreateSigningKey(PublicKey!, PrivateKey!);
        if (AllowedEndpointHosts.Count == 0)
        {
            throw new InvalidOperationException("The assistant push endpoint allow-list must not be empty.");
        }
    }
}

/// <summary>
/// Creates and reads VAPID key pairs for <see cref="NhAssistantPushOptions"/>.
/// </summary>
public static class NhAssistantWebPushKeys
{
    /// <summary>
    /// Generates a new P-256 key pair as base64url strings. Store the private key as a secret; changing
    /// the pair makes browsers subscribe again.
    /// </summary>
    public static (string PublicKey, string PrivateKey) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(includePrivateParameters: true);
        return (
            NhAssistantBase64Url.Encode(UncompressedPoint(parameters.Q)),
            NhAssistantBase64Url.Encode(parameters.D!));
    }

    internal static ECDsa CreateSigningKey(string publicKey, string privateKey)
    {
        byte[] point;
        byte[] scalar;
        try
        {
            point = NhAssistantBase64Url.Decode(publicKey);
            scalar = NhAssistantBase64Url.Decode(privateKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("The assistant push keys must be base64url.", exception);
        }
        if (point.Length != 65 || point[0] != 0x04 || scalar.Length != 32)
        {
            throw new InvalidOperationException(
                "The assistant push public key must be an uncompressed P-256 point and the private key a 32-byte scalar.");
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = point[1..33], Y = point[33..65] },
                D = scalar
            });
        }
        catch (CryptographicException exception)
        {
            key.Dispose();
            throw new InvalidOperationException("The assistant push public and private keys do not form a P-256 key pair.", exception);
        }
        return key;
    }

    internal static byte[] UncompressedPoint(ECPoint point)
    {
        var result = new byte[65];
        result[0] = 0x04;
        point.X!.CopyTo(result, 1);
        point.Y!.CopyTo(result, 33);
        return result;
    }
}

internal static class NhAssistantBase64Url
{
    public static string Encode(ReadOnlySpan<byte> value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Decodes base64url, with or without padding; standard base64 is accepted as well. Throws
    /// <see cref="FormatException"/> for other input.
    /// </summary>
    public static byte[] Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.Trim().TrimEnd('=').Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("The value has an invalid base64url length.")
        };
        return Convert.FromBase64String(normalized);
    }
}

internal static class NhAssistantPushLimits
{
    public const int MaxEndpointLength = 2_048;
}
