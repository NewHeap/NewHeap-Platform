using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NewHeap.Platform.Common.Models.Options;

/// <summary>
/// Refuses <see cref="MailServiceSettings"/> whose <see cref="MailServiceSettings.SecurityMode"/> is not
/// an <see cref="NhSmtpSecurityMode"/>, such as a number that configuration bound without a name. The
/// registrations of the mail service validate on start, so such settings stop the host instead of
/// the first delivery.
/// </summary>
internal sealed class NhMailServiceSettingsValidator : IValidateOptions<MailServiceSettings>
{
    public ValidateOptionsResult Validate(string? name, MailServiceSettings options)
    {
        if (options.SecurityMode is { } securityMode && !Enum.IsDefined(securityMode))
        {
            return ValidateOptionsResult.Fail(UndefinedSecurityModeMessage(securityMode));
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Validates <see cref="MailServiceSettings"/> when the host starts. Every registration of
    /// <see cref="Services.NhMailService"/> calls it; calling it again changes nothing.
    /// </summary>
    internal static void Register(IServiceCollection services)
    {
        services.AddOptions<MailServiceSettings>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<MailServiceSettings>, NhMailServiceSettingsValidator>());
    }

    internal static string UndefinedSecurityModeMessage(NhSmtpSecurityMode securityMode)
    {
        return $"MailServiceSettings.SecurityMode {(int)securityMode} is not an {nameof(NhSmtpSecurityMode)}. " +
               $"Use {nameof(NhSmtpSecurityMode.StartTlsRequired)}, {nameof(NhSmtpSecurityMode.ImplicitTls)} " +
               $"or {nameof(NhSmtpSecurityMode.None)}, or leave it out to follow EnableSsl.";
    }
}
