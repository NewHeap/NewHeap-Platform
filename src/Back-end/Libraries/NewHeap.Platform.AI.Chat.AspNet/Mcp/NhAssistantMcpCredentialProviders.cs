using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI.Chat.Entities;
using NewHeap.Platform.AI.Mcp;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AI.Chat.AspNet.Mcp;

/// <summary>
/// Whose credential an authentication mode that an application registers sends.
/// </summary>
public enum NhAssistantMcpCredentialOwnership
{
    /// <summary>
    /// One credential of the organization that every caller shares, as <c>bearer</c> or
    /// <c>api-key</c>. Connections are shared, and an administrator may allow the server in
    /// background runs with <c>headlessUse: organization</c>, where its tools accept agent grants.
    /// </summary>
    Organization = 0,

    /// <summary>
    /// Each person's own credential, as <c>forward-user-token</c>, for example a token exchanged for
    /// the person at the application's identity provider. Connections are never shared, and an
    /// administrator may allow the server in background runs with <c>headlessUse: personal</c>, where
    /// it serves only the person a run acts for, after that person consented.
    /// </summary>
    Person = 1
}

/// <summary>
/// Registers authentication modes of MCP servers in code (D1): the application's own
/// <see cref="INhAssistantMcpCredentialProvider"/>, which administrators then choose in the server
/// editor next to the built-in modes, for example a token exchange with the application's identity
/// provider. The provider supplies the headers of every request; the assistant keeps the host checks,
/// the renewal after <c>401</c>, the consent to background use and the audit as for the built-in modes.
/// </summary>
public static partial class NhAssistantMcpCredentialProviders
{
    /// <summary>The longest name of an authentication mode.</summary>
    public const int MaxModeLength = 40;

    /// <summary>
    /// Registers <typeparamref name="TProvider"/> as a singleton that supplies the credentials of
    /// servers with the authentication mode <paramref name="mode"/>. The mode is dash-case, at most
    /// <see cref="MaxModeLength"/> characters, not one of <see cref="NhAssistantMcpAuthModes"/> and
    /// registered once. The provider resolves scoped services through an
    /// <see cref="IServiceScopeFactory"/>, because a shared connection keeps it for its lifetime, and
    /// returns <see langword="true"/> from <see cref="INhAssistantMcpCredentialProvider.Supports"/> for
    /// <paramref name="mode"/>. A server of the mode takes no secret, user name or OAuth settings in the
    /// editor: the provider reads what it needs from the application's configuration, for example with
    /// <c>NhConfigurationSecrets.Read</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The mode is not dash-case, too long or a built-in mode.</exception>
    /// <exception cref="InvalidOperationException">The mode is already registered.</exception>
    public static NhAssistantBuilder AddMcpCredentialProvider<TProvider>(
        this NhAssistantBuilder builder,
        string mode,
        NhAssistantMcpCredentialOwnership ownership = NhAssistantMcpCredentialOwnership.Organization)
        where TProvider : class, INhAssistantMcpCredentialProvider
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (string.IsNullOrEmpty(mode) || mode.Length > MaxModeLength || !ModePattern().IsMatch(mode))
        {
            throw new ArgumentException($"An MCP authentication mode is dash-case, starts with a letter and has at most {MaxModeLength} characters.", nameof(mode));
        }
        if (NhAssistantMcpAuthModes.IsValid(mode))
        {
            throw new ArgumentException($"'{mode}' is a built-in MCP authentication mode.", nameof(mode));
        }
        if (!Enum.IsDefined(ownership))
        {
            throw new ArgumentOutOfRangeException(nameof(ownership), ownership, "Unknown credential ownership.");
        }
        if (builder.Services.Any(descriptor => descriptor.ImplementationInstance is NhAssistantMcpAuthModeRegistration registration
            && registration.Mode == mode))
        {
            throw new InvalidOperationException($"An MCP credential provider for the mode '{mode}' is already registered.");
        }

        builder.Services.TryAddSingleton<TProvider>();
        builder.Services.AddSingleton<INhAssistantMcpCredentialProvider>(provider => provider.GetRequiredService<TProvider>());
        builder.Services.AddSingleton(new NhAssistantMcpAuthModeRegistration(mode, typeof(TProvider), ownership));
        return builder;
    }

    /// <summary>
    /// A credential that cannot be obtained, with a stable code such as
    /// <see cref="NhAssistantAdminErrorCodes.McpUnauthorized"/> or one of the provider's own. The
    /// request is not sent, the server is left out of the turn with this code, and a tool call fails
    /// with it as certainly not run.
    /// </summary>
    public static TaskResult<NhAssistantMcpCredential> Failed(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return NhAssistantMcpCredentialResults.Failed(code);
    }

    /// <summary>
    /// The rule for a credential that belongs to one person (<see cref="NhAssistantMcpCredentialOwnership.Person"/>):
    /// in a person's request, a call the gate authorized with that person's own or delegated rights; in
    /// a headless turn, only a call with exactly the rights the turn's person lends
    /// (<see cref="NhAssistantMcpCredentialSubject.PersonGrantSource"/>), never an agent grant. Any other
    /// call fails with <see cref="NhAssistantAdminErrorCodes.McpCredentialMismatch"/> before it is sent.
    /// </summary>
    public static TaskResult AuthorizePersonalCall(NhAssistantMcpCredentialSubject subject, NhAiMcpCallContext call)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(call);
        var allowed = subject.IsHeadless
            ? call.Invocation.GrantSource == subject.PersonGrantSource
            : call.Invocation.GrantSource is NhAiGrantSource.None or NhAiGrantSource.Delegated;
        return allowed
            ? TaskResult.Succeeded()
            : TaskResult.Failed(
                NhAssistantAdminErrorCodes.McpCredentialMismatch,
                "The call was not authorized for the person whose credential the connection carries.");
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModePattern();
}

/// <summary>An authentication mode an application registered, its provider and whose credential it sends.</summary>
internal sealed record NhAssistantMcpAuthModeRegistration(
    string Mode,
    Type ProviderType,
    NhAssistantMcpCredentialOwnership Ownership);

/// <summary>
/// The authentication modes of MCP servers: the built-in <see cref="NhAssistantMcpAuthModes"/> and the
/// modes applications registered with
/// <see cref="NhAssistantMcpCredentialProviders.AddMcpCredentialProvider{TProvider}"/>. The rules that
/// depend on a mode (valid, shared or per person, headless use) are asked here for every server.
/// </summary>
internal sealed class NhAssistantMcpAuthModeCatalog(IEnumerable<NhAssistantMcpAuthModeRegistration> registrations)
{
    private readonly Dictionary<string, NhAssistantMcpAuthModeRegistration> _registered = registrations
        .ToDictionary(registration => registration.Mode, StringComparer.Ordinal);

    /// <summary>The modes applications registered, by name.</summary>
    public IReadOnlyList<NhAssistantMcpAuthModeRegistration> Registered => [.. _registered.Values.OrderBy(item => item.Mode, StringComparer.Ordinal)];

    public NhAssistantMcpAuthModeRegistration? Find(string? mode)
    {
        return mode is not null && _registered.TryGetValue(mode, out var registration) ? registration : null;
    }

    public bool IsValid(string? mode)
    {
        return NhAssistantMcpAuthModes.IsValid(mode) || Find(mode) is not null;
    }

    /// <summary>Whether every caller of the server shares one credential of the organization.</summary>
    public bool IsOrganizationCredential(string? mode, string? connectionScope)
    {
        return Find(mode) is { } registration
            ? registration.Ownership == NhAssistantMcpCredentialOwnership.Organization
            : NhAssistantMcpAuthModes.IsOrganizationCredential(mode, connectionScope);
    }

    /// <summary>
    /// Whether the headless use fits the mode: <c>organization</c> a credential of the organization,
    /// <c>personal</c> a person's credential.
    /// </summary>
    public bool FitsHeadlessUse(string headlessUse, string mode, string? connectionScope)
    {
        if (Find(mode) is not { } registration)
        {
            return NhAssistantMcpHeadlessUses.FitsAuthMode(headlessUse, mode, connectionScope);
        }
        return headlessUse switch
        {
            NhAssistantMcpHeadlessUses.Never => true,
            NhAssistantMcpHeadlessUses.Organization => registration.Ownership == NhAssistantMcpCredentialOwnership.Organization,
            NhAssistantMcpHeadlessUses.Personal => registration.Ownership == NhAssistantMcpCredentialOwnership.Person,
            _ => false
        };
    }

    /// <summary>
    /// The provider of a server's mode: for a registered mode the registered provider, for a built-in
    /// mode the last registered provider that supports it, so a host or test can replace a built-in.
    /// </summary>
    public INhAssistantMcpCredentialProvider? ProviderFor(string mode, IReadOnlyList<INhAssistantMcpCredentialProvider> providers)
    {
        if (Find(mode) is { } registration)
        {
            return providers.LastOrDefault(candidate => candidate.GetType() == registration.ProviderType && candidate.Supports(mode));
        }
        return providers.LastOrDefault(candidate => !_registered.Values.Any(item => item.ProviderType == candidate.GetType())
            && candidate.Supports(mode));
    }
}
