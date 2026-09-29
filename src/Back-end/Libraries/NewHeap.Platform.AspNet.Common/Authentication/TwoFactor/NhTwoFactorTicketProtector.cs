using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text.Json;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

internal static class NhTwoFactorTicketPurposes
{
    internal const string Challenge = "challenge";
}

/// <summary>
/// Content of a protected two-factor ticket. The security stamp binds the ticket to the
/// account state; any credential change invalidates outstanding tickets.
/// </summary>
internal sealed record NhTwoFactorTicket(
    Guid UserId,
    string SecurityStamp,
    string Purpose,
    string Factor,
    string Nonce,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Protects two-factor tickets with ASP.NET Core Data Protection. Every purpose uses its
/// own protector, so a ticket issued for one step cannot be replayed for another.
/// </summary>
internal sealed class NhTwoFactorTicketProtector
{
    private const string ProtectorPurpose = "NewHeap.Platform.AspNet.Common.TwoFactor.Ticket.v1";

    private readonly IDataProtectionProvider _dataProtectionProvider;
    private readonly TimeProvider _timeProvider;

    public NhTwoFactorTicketProtector(IDataProtectionProvider dataProtectionProvider, TimeProvider timeProvider)
    {
        _dataProtectionProvider = dataProtectionProvider;
        _timeProvider = timeProvider;
    }

    internal string Protect(NhTwoFactorTicket ticket)
    {
        var payload = JsonSerializer.Serialize(ticket);
        return CreateProtector(ticket.Purpose).Protect(payload, ticket.ExpiresAt);
    }

    internal NhTwoFactorTicket? Unprotect(string? protectedTicket, string expectedPurpose)
    {
        if (string.IsNullOrWhiteSpace(protectedTicket))
        {
            return null;
        }

        string payload;
        try
        {
            payload = CreateProtector(expectedPurpose).Unprotect(protectedTicket, out _);
        }
        catch (CryptographicException)
        {
            return null;
        }

        NhTwoFactorTicket? ticket;
        try
        {
            ticket = JsonSerializer.Deserialize<NhTwoFactorTicket>(payload);
        }
        catch (JsonException)
        {
            return null;
        }

        if (ticket == null || ticket.Purpose != expectedPurpose)
        {
            return null;
        }

        if (ticket.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            return null;
        }

        return ticket;
    }

    private ITimeLimitedDataProtector CreateProtector(string purpose)
    {
        return _dataProtectionProvider
            .CreateProtector(ProtectorPurpose, purpose)
            .ToTimeLimitedDataProtector();
    }
}
