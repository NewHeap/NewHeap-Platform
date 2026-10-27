namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// Decides whether a sign-in flow may send the browser to a return URL that a client supplied.
/// A return URL is allowed when it is a path on the current host, such as <c>/projects</c>, or an
/// absolute <c>http</c> or <c>https</c> URL whose origin is one of the allowed origins. Anything
/// else, such as <c>//host</c>, <c>/\host</c>, another scheme, user information or an origin that
/// is not listed, is refused, so a flow cannot be used to send the browser, or a token in the URL
/// fragment, to a site the application does not own.
/// </summary>
public static class NhReturnUrls
{
    /// <summary>
    /// Whether <paramref name="returnUrl"/> is a path on the current host or an absolute URL on one
    /// of <paramref name="allowedOrigins"/>. Origins compare by scheme, host and port, ignoring case
    /// and a trailing slash; an entry that is not an absolute URL allows nothing.
    /// </summary>
    public static bool IsAllowed(string? returnUrl, IEnumerable<string>? allowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return false;
        }

        if (returnUrl.Any(character => char.IsControl(character) || character == '\\'))
        {
            return false;
        }

        if (returnUrl[0] == '/')
        {
            // "//host" is a network-path reference to another host.
            return returnUrl.Length == 1 || returnUrl[1] != '/';
        }

        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(target.UserInfo))
        {
            return false;
        }

        var targetOrigin = target.GetLeftPart(UriPartial.Authority);
        foreach (var allowedOrigin in allowedOrigins ?? [])
        {
            if (Uri.TryCreate(allowedOrigin?.Trim(), UriKind.Absolute, out var origin)
                && string.Equals(origin.GetLeftPart(UriPartial.Authority), targetOrigin, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
