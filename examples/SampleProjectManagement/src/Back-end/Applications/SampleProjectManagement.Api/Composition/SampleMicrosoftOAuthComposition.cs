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
/// </remarks>
public static class SampleMicrosoftOAuthComposition
{
    public const string SettingsSection = "NewHeap:PlatformCommon:MicrosoftAuthSettings";

    public static void ConfigureSampleMicrosoftOAuth(this MicrosoftAuthSettings settings, IConfiguration configuration)
    {
        configuration.GetSection(SettingsSection).Bind(settings);
    }
}
