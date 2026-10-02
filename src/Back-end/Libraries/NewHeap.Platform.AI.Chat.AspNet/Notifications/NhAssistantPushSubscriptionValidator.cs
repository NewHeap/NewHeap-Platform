using NewHeap.Platform.AI.Chat.Notifications;

namespace NewHeap.Platform.AI.Chat.AspNet.Notifications;

internal sealed record NhAssistantValidPushSubscription(
    string Endpoint,
    string P256dh,
    string Auth,
    string Language);

/// <summary>
/// Accepts only browser subscriptions of allowed push services, so the server never posts to an
/// address a client chose: https on the default port, no user info and a host on the allow-list.
/// </summary>
internal static class NhAssistantPushSubscriptionValidator
{
    public static NhAssistantValidPushSubscription? Validate(
        NhAssistantPushSubscriptionRequest? request,
        NhAssistantPushOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (request?.Endpoint is not { } endpoint
            || request.Keys?.P256dh is not { Length: <= 128 } p256dh
            || request.Keys.Auth is not { Length: <= 64 } auth
            || !TryGetAllowedEndpoint(endpoint, options.AllowedEndpointHosts, out _))
        {
            return null;
        }

        byte[] publicKey;
        byte[] secret;
        try
        {
            publicKey = NhAssistantBase64Url.Decode(p256dh);
            secret = NhAssistantBase64Url.Decode(auth);
        }
        catch (FormatException)
        {
            return null;
        }
        if (secret.Length != 16 || !NhAssistantWebPushEncryption.IsValidPublicKey(publicKey))
        {
            return null;
        }

        var language = request.Language?.StartsWith("nl", StringComparison.OrdinalIgnoreCase) == true ? "nl" : "en";
        return new NhAssistantValidPushSubscription(
            endpoint,
            NhAssistantBase64Url.Encode(publicKey),
            NhAssistantBase64Url.Encode(secret),
            language);
    }

    public static bool TryGetAllowedEndpoint(
        string endpoint,
        IEnumerable<string> allowedHosts,
        out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(endpoint)
            || endpoint.Length > NhAssistantPushLimits.MaxEndpointLength
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps
            || !parsed.IsDefaultPort
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || parsed.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        var host = parsed.IdnHost.TrimEnd('.');
        foreach (var allowed in allowedHosts)
        {
            var pattern = allowed.Trim().TrimEnd('.');
            var matches = pattern.StartsWith("*.", StringComparison.Ordinal)
                ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
                    && host.Length > pattern.Length - 1
                : string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
            if (matches)
            {
                uri = parsed;
                return true;
            }
        }
        return false;
    }
}
