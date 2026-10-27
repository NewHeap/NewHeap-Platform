namespace NewHeap.Platform.Common.Identity.Claims;

public static partial class NhPlatformClaimTypes
{
    public const string Permission = "nh.platform.permission";
    public const string DivisionRole = "nh.platform.division.role";
    public const string DivisionPermission = "nh.platform.division.permission";

    /// <summary>
    /// Membership of a division, with the division id as its value. NewHeap Identity adds one for
    /// every division the user is a member of, also when the membership has no division role, but
    /// none while the membership is locked out; the division access-all permission makes the user a
    /// member of every division. A division check requires membership before any division role or
    /// permission counts.
    /// </summary>
    public const string DivisionMember = "nh.platform.division.member";

    public const string ImpersonateOriginUserId = "nh.platform.auth.impersonate.origin-user-id";

    /// <summary>
    /// A digest of the security stamp the impersonating user had when the impersonation started. The
    /// session ends when that user's stamp changes, for example after a password change.
    /// </summary>
    public const string ImpersonateOriginSecurityStamp = "nh.platform.auth.impersonate.origin-security-stamp";
}