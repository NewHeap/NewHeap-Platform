using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// Takes part in consultation chains across applications (SPM-366). The API names itself in the chains
/// it sends, the research binding of <see cref="SampleAssistantResearch"/> sends the chain of the
/// calling turn, and a reviewed partner application, whose assistant consults the intake reviewer over
/// MCP, continues its own chain here: a consultation the partner starts begins one level deeper than
/// the partner's and counts the partner's agents as askers, so a loop between the two applications is
/// refused with <c>assistant-consult-depth-exceeded</c> before the reviewer runs. The chain of any other
/// MCP caller is ignored, never refused.
/// </summary>
/// <remarks>
/// It is loop protection between cooperating applications, not authorization: the partner's person
/// still needs the consultation's policy and a role on the project, and the reviewer's daily budget,
/// the daily consultation limit per person and the call's deadline bound every call. Without
/// <see cref="ApplicationSetting"/> the API takes part in no chain; without the partner's settings it
/// sends its chain but continues none.
/// </remarks>
public static class SampleConsultationChain
{
    /// <summary>The API's own name in consultation chains, for example its public origin.</summary>
    public const string ApplicationSetting = "Sample:Assistant:ConsultationChain:Application";

    /// <summary>The partner application's name in consultation chains, as the partner names itself.</summary>
    public const string PartnerApplicationSetting = "Sample:Assistant:ConsultationChain:PartnerApplication";

    /// <summary>The issuer of the partner's tokens, which its MCP calls carry as their <c>iss</c> claim.</summary>
    public const string PartnerIssuerSetting = "Sample:Assistant:ConsultationChain:PartnerIssuer";

    /// <summary>
    /// Whether the API has a name in consultation chains, so its bindings may send the chain.
    /// </summary>
    public static bool IsNamed(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[ApplicationSetting]);
    }

    public static NhAssistantBuilder AddSampleConsultationChain(
        this NhAssistantBuilder assistant,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!IsNamed(configuration))
        {
            return assistant;
        }

        assistant.UseMcpChainApplication(configuration[ApplicationSetting]!);
        var partner = configuration[PartnerApplicationSetting];
        var issuer = configuration[PartnerIssuerSetting];
        if (string.IsNullOrWhiteSpace(partner) || string.IsNullOrWhiteSpace(issuer))
        {
            return assistant;
        }

        // Only the partner's own MCP calls are believed: the request's principal carries exactly its
        // issuer. A shared issuer would name the partner's client too (ClientClaimType, ClientId).
        return assistant.ContinueMcpChain(new NhAssistantMcpChainCaller(partner, issuer));
    }
}
