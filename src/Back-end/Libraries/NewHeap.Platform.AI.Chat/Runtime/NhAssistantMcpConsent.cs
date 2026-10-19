using System.Security.Cryptography;
using System.Text;
using NewHeap.Platform.AI.Chat.Entities;

namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// A person's consent to background use of an MCP server. It holds for the server as it was when
/// the person consented: a changed id, URL or authentication mode needs consent again.
/// </summary>
internal static class NhAssistantMcpConsent
{
    public static string HashOf(AssistantMcpServer server)
    {
        var identity = server.Id + "\n" + server.Url.Trim() + "\n" + server.AuthMode;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static bool Allows(AssistantMcpConnection? connection, AssistantMcpServer server)
    {
        return connection is { AllowBackgroundUse: true, ConsentHash: { } hash }
            && string.Equals(hash, HashOf(server), StringComparison.Ordinal);
    }
}
