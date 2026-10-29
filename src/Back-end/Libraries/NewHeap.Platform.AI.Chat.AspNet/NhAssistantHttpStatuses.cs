using Microsoft.AspNetCore.Http;

namespace NewHeap.Platform.AI.Chat.AspNet;

/// <summary>
/// The HTTP status of an assistant code, as every assistant endpoint answers it (FB53). An application
/// that serves a channel through its own API and the assistant's services, such as
/// <see cref="INhAssistantConversationTurns"/> and <see cref="INhAssistantConversations"/>, answers a
/// failed result with the status the endpoints would give its code:
/// <code>
/// var code = result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
/// return StatusCode(NhAssistantHttpStatuses.Of(code ?? NhAssistantErrorCodes.TurnFailed), new { code });
/// </code>
/// The endpoints use this mapping themselves, so a channel and the endpoints answer a code alike. Map
/// the application's own codes first: a code the assistant does not know answers
/// <see cref="UnknownCodeStatus"/>.
/// </summary>
public static class NhAssistantHttpStatuses
{
    /// <summary>
    /// The status of a code the assistant does not know, such as one an application's resolver or
    /// channel returned: <c>400</c>.
    /// </summary>
    public const int UnknownCodeStatus = StatusCodes.Status400BadRequest;

    /// <summary>
    /// The HTTP status the assistant's endpoints answer <paramref name="code"/> with, such as <c>404</c>
    /// for <c>assistant-conversation-not-found</c> and <c>409</c> for <c>assistant-conversation-busy</c>;
    /// <see cref="UnknownCodeStatus"/> (<c>400</c>) for a code the assistant does not know. Every code of
    /// <see cref="NhAssistantErrorCodes"/> and <see cref="NhAssistantAdminErrorCodes"/> has exactly one
    /// status.
    /// </summary>
    /// <param name="code">The code of a failed result.</param>
    /// <exception cref="ArgumentException"><paramref name="code"/> is <see langword="null"/>, empty or
    /// white space.</exception>
    public static int Of(string code)
    {
        return NhAssistantHttpErrors.StatusOf(code);
    }
}
