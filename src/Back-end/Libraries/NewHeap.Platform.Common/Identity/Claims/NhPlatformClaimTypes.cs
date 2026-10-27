namespace NewHeap.Platform.Common.Identity.Claims;

public static partial class NhPlatformClaimTypes
{
    public const string Permission = "nh.platform.permission";
    public const string DivisionRole = "nh.platform.division.role";
    public const string DivisionPermission = "nh.platform.division.permission";
    public const string ImpersonateOriginUserId = "nh.platform.auth.impersonate.origin-user-id";

    /// <summary>
    /// The security stamp the impersonating user had when the impersonation started. The session
    /// ends when that user's stamp changes, for example after a password change.
    /// </summary>
    public const string ImpersonateOriginSecurityStamp = "nh.platform.auth.impersonate.origin-security-stamp";
}