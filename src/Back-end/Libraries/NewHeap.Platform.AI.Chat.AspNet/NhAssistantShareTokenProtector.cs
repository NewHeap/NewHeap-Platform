using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using NewHeap.Platform.AI.Chat.Collaboration;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// Protects invitation-link tokens with ASP.NET Data Protection, so the owner can copy the link again
/// while a database copy alone does not reveal it. Hosts with several instances must share their
/// Data Protection key ring, as for MCP server secrets.
/// </summary>
internal sealed class NhAssistantShareTokenProtector(IDataProtectionProvider provider) : INhAssistantShareTokenProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("NewHeap.Platform.AI.Chat.ShareLink.v1");

    public string Protect(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return _protector.Protect(token);
    }

    public bool Matches(string protectedToken, string candidate)
    {
        var token = Unprotect(protectedToken);
        if (token is null || string.IsNullOrEmpty(candidate))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(candidate));
    }

    public string? Unprotect(string protectedToken)
    {
        try
        {
            return _protector.Unprotect(protectedToken);
        }
        catch (CryptographicException)
        {
            // A lost or rotated-out key makes the link invalid; the owner creates a new one.
            return null;
        }
    }
}
