using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models.Options;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-355: the API's mail settings name their SMTP security mode. <c>appsettings.json</c> sends in
/// plain text (<see cref="NhSmtpSecurityMode.None"/>) to a development mail server on
/// <c>localhost:1025</c>; a deployment overrides <c>SecurityMode</c>, <c>Host</c>, <c>Port</c> and the
/// credentials, for example <c>StartTlsRequired</c> on port 587 or <c>ImplicitTls</c> on port 465.
/// A mode that does not exist, such as <c>StartTlsWhenAvailable</c>, stops the host at startup.
/// </summary>
public static class SampleMailComposition
{
    public const string SectionName = "NewHeap:PlatformCommon:MailServiceSettings";

    /// <summary>
    /// Registers <c>NhMailService</c> with the settings of <see cref="SectionName"/>.
    /// </summary>
    public static NewHeapPlatformCommonConfigurator WithSampleMail(
        this NewHeapPlatformCommonConfigurator common,
        IConfiguration configuration)
    {
        return common.WithMail(mail => configuration.GetSection(SectionName).Bind(mail));
    }
}
