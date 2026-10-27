using NewHeap.Platform.Common.Models.Options;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-062: Microsoft sign-in for the accounts of the <c>sample.local</c> domain. The settings come
/// from <see cref="SettingsSection"/>, the client id and secret from the secrets file.
/// </summary>
/// <remarks>
/// The management app starts the sign-in through its <c>/api</c> proxy, and the configured
/// <c>CallbackUrl</c> is the callback behind that same proxy, so the correlation cookie that binds
/// the sign-in to the browser reaches the callback. The callback returns only to a path of the API
/// or to one of the sample's origins in <c>NewHeap:PlatformAspNetCommon:Settings:AllowedOrigins</c>.
/// <para>
/// A sign-in finds its local account by the Microsoft account's tenant and object id, which the
/// first sign-in stores as an Identity login. The <c>common</c> authority accepts every directory,
/// so the shipped settings link no account by mail: name your directory in <c>AllowedTenantIds</c>,
/// or set <c>TenantId</c> to its id, and the first sign-in of an account of that directory links the
/// local account whose user name is its mail address, once. Another Microsoft account with the same
/// mail, in that directory or another one, never reaches the linked account.
/// </para>
/// </remarks>
public static class SampleMicrosoftOAuthComposition
{
    public const string SettingsSection = "NewHeap:PlatformCommon:MicrosoftAuthSettings";

    public static void ConfigureSampleMicrosoftOAuth(this MicrosoftAuthSettings settings, IConfiguration configuration)
    {
        configuration.GetSection(SettingsSection).Bind(settings);
    }
}
