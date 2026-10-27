using Microsoft.AspNetCore.Identity;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

namespace NewHeap.Platform.AspNet.Common.Authentication;

/// <summary>
/// The Identity external login (<c>AspNetUserLogins</c>) that links a local account to a Microsoft
/// account. The provider key is the account's directory (tenant) id and object id, which Microsoft
/// never reassigns, so a sign-in finds its local account by who signed in, not by a changeable
/// attribute such as the mail address. An application links accounts itself with
/// <c>UserManager.AddLoginAsync(user, NhMicrosoftOAuthLogins.LoginInfo(tenantId, objectId))</c>
/// and unlinks them with <c>RemoveLoginAsync</c>.
/// </summary>
public static class NhMicrosoftOAuthLogins
{
    /// <summary>The login provider of the link, the same value as <see cref="NhAuthenticationFactors.MicrosoftOAuth"/>.</summary>
    public const string LoginProvider = NhAuthenticationFactors.MicrosoftOAuth;

    public const string ProviderDisplayName = "Microsoft";

    /// <summary>
    /// The provider key of a Microsoft account: its tenant id and object id as lowercase GUIDs,
    /// separated by <c>/</c>.
    /// </summary>
    public static string ProviderKey(string tenantId, string objectId)
    {
        return Normalize(tenantId, nameof(tenantId)) + "/" + Normalize(objectId, nameof(objectId));
    }

    /// <summary>The login to add to a local account to link it to the Microsoft account.</summary>
    public static UserLoginInfo LoginInfo(string tenantId, string objectId)
    {
        return new UserLoginInfo(LoginProvider, ProviderKey(tenantId, objectId), ProviderDisplayName);
    }

    private static string Normalize(string id, string parameterName)
    {
        if (!Guid.TryParse(id, out var value))
        {
            throw new ArgumentException("A Microsoft tenant or object id is a GUID.", parameterName);
        }

        return value.ToString("D");
    }
}
