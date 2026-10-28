using System.ComponentModel.DataAnnotations;

namespace SampleProjectManagement.Api.Models;

/// <summary>
/// SPM-332: the chat a signed-in person continues in the messaging app.
/// </summary>
public sealed class AssistantChannelLinkMutateModel
{
    /// <summary>
    /// The person's chat with the project assistant.
    /// </summary>
    [Required]
    public Guid ConversationId { get; set; }
}

/// <summary>
/// SPM-332: a message the messaging provider delivers to <c>POST assistant-channel/messages</c>.
/// </summary>
public sealed class AssistantChannelMessageMutateModel
{
    /// <summary>
    /// The provider's id of the delivery; a delivery repeated with the same id starts no second turn.
    /// </summary>
    [Required]
    [MaxLength(128)]
    public string DeliveryId { get; set; } = "";

    /// <summary>
    /// The link token of the chat the person writes in.
    /// </summary>
    [Required]
    [MaxLength(4_000)]
    public string Token { get; set; } = "";

    /// <summary>
    /// The person's message.
    /// </summary>
    [Required]
    [MaxLength(8_000)]
    public string Text { get; set; } = "";

    /// <summary>
    /// SPM-358: the attachments <c>POST assistant-channel/files</c> uploaded to the chat, as it answered
    /// them; the turn opens them with the person's rights.
    /// </summary>
    [MaxLength(5)]
    public List<AssistantChannelFileViewModel> Files { get; set; } = [];
}

/// <summary>
/// SPM-358: a page of the person's chats the messaging app lists (<c>POST assistant-channel/chat-list</c>).
/// </summary>
public sealed class AssistantChannelChatListMutateModel
{
    /// <summary>
    /// A link token of the person, of any of their linked chats.
    /// </summary>
    [Required]
    [MaxLength(4_000)]
    public string Token { get; set; } = "";

    /// <summary>
    /// The page, from 1.
    /// </summary>
    [Range(1, 1_000)]
    public int Page { get; set; } = 1;
}

/// <summary>
/// SPM-358: an attachment the messaging provider delivers to <c>POST assistant-channel/files</c> as
/// <c>multipart/form-data</c>: the chat's link token and one file part.
/// </summary>
public sealed class AssistantChannelFileMutateModel
{
    /// <summary>
    /// The link token of the chat the attachment is for.
    /// </summary>
    [Required]
    [MaxLength(4_000)]
    public string Token { get; set; } = "";

    /// <summary>
    /// The attachment, with the name and media type the person's device gave it.
    /// </summary>
    [Required]
    public IFormFile? File { get; set; }
}

/// <summary>
/// SPM-332: an answer to an approval card the messaging provider delivers to
/// <c>POST assistant-channel/decisions</c>.
/// </summary>
public sealed class AssistantChannelDecisionMutateModel
{
    /// <summary>
    /// The link token of the chat the card belongs to.
    /// </summary>
    [Required]
    [MaxLength(4_000)]
    public string Token { get; set; } = "";

    /// <summary>
    /// The approval the card shows.
    /// </summary>
    [Required]
    public Guid ApprovalId { get; set; }

    /// <summary>
    /// The proposal hash the card shows; a proposal that changed since is refused.
    /// </summary>
    [Required]
    [MaxLength(128)]
    public string ProposalHash { get; set; } = "";

    /// <summary>
    /// The answer: yes or approve, no or reject.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Text { get; set; } = "";
}

/// <summary>
/// SPM-341: a link token of the person the messaging provider delivers to read or stop the chat it names
/// (<c>POST assistant-channel/chat</c>, <c>POST assistant-channel/stops</c>).
/// </summary>
public sealed class AssistantChannelTokenMutateModel
{
    /// <summary>
    /// The link token of the chat.
    /// </summary>
    [Required]
    [MaxLength(4_000)]
    public string Token { get; set; } = "";
}

/// <summary>
/// SPM-341: a new chat the person starts from the messaging app (<c>POST assistant-channel/chats</c>).
/// </summary>
public sealed class AssistantChannelChatMutateModel
{
    /// <summary>
    /// A link token of the person, of any of their linked chats.
    /// </summary>
    [Required]
    [MaxLength(4_000)]
    public string Token { get; set; } = "";

    /// <summary>
    /// The agent the person chats with, such as the status advisor.
    /// </summary>
    [Required]
    [MaxLength(128)]
    public string AgentId { get; set; } = "";

    /// <summary>
    /// The chat's title; without one the first message names it.
    /// </summary>
    [MaxLength(200)]
    public string? Title { get; set; }
}

/// <summary>
/// SPM-341: an answer to a question card the messaging provider delivers to
/// <c>POST assistant-channel/answers</c>.
/// </summary>
public sealed class AssistantChannelQuestionAnswerMutateModel
{
    /// <summary>
    /// The link token of the chat the card belongs to.
    /// </summary>
    [Required]
    [MaxLength(4_000)]
    public string Token { get; set; } = "";

    /// <summary>
    /// The question the card shows.
    /// </summary>
    [Required]
    public Guid QuestionId { get; set; }

    /// <summary>
    /// The reply: the number of a choice as the card shows them, from 1, or the choice's text.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Text { get; set; } = "";
}

/// <summary>
/// SPM-332: the link token the messaging app keeps for the chat, and until when it is valid.
/// </summary>
public sealed record AssistantChannelLinkViewModel(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// SPM-332: what the messaging app shows after a turn.
/// </summary>
/// <param name="Status">completed, waiting-for-approval, cancelled or failed.</param>
/// <param name="Code">The code of a failed turn, or of a turn that reached its tool-call limit.</param>
/// <param name="Text">The assistant's answer.</param>
/// <param name="Approval">The approval card when the turn waits for the person.</param>
/// <param name="Question">SPM-341: the question card when the turn waits for the person's answer.</param>
public sealed record AssistantChannelAnswerViewModel(
    string Status,
    string? Code,
    string? Text,
    AssistantChannelApprovalViewModel? Approval,
    AssistantChannelQuestionViewModel? Question = null);

/// <summary>
/// SPM-341: a question card: the question, and the choices the card numbers from 1.
/// </summary>
public sealed record AssistantChannelQuestionViewModel(Guid QuestionId, string Question, IReadOnlyList<string> Choices);

/// <summary>
/// SPM-341: a chat as the messaging app shows it.
/// </summary>
/// <param name="Status">idle, running, waiting-for-approval or failed.</param>
/// <param name="Title">The chat's title.</param>
/// <param name="LastAnswer">The assistant's last answer.</param>
/// <param name="Approval">The approval card the chat waits for.</param>
/// <param name="Question">The question card the chat waits for.</param>
public sealed record AssistantChannelChatStateViewModel(
    string Status,
    string? Title,
    string? LastAnswer,
    AssistantChannelApprovalViewModel? Approval,
    AssistantChannelQuestionViewModel? Question);

/// <summary>
/// SPM-341: a new chat: the link token the messaging app keeps for it, until when it is valid, and the
/// chat.
/// </summary>
public sealed record AssistantChannelChatViewModel(string Token, DateTimeOffset ExpiresAt, AssistantChannelChatStateViewModel Chat);

/// <summary>
/// SPM-358: an attachment of the chat as the platform stored it: the reference a message carries.
/// </summary>
/// <param name="Id">The media item's id.</param>
/// <param name="Name">The file's name without a path.</param>
/// <param name="MediaType">The media type it was checked and stored as.</param>
/// <param name="Size">The length in bytes.</param>
/// <param name="Hash">The hash the media library gave it, when it gave one.</param>
public sealed record AssistantChannelFileViewModel(string Id, string Name, string MediaType, long Size, string? Hash = null);

/// <summary>
/// SPM-358: a page of the person's chats and how many there are.
/// </summary>
public sealed record AssistantChannelChatListViewModel(IReadOnlyList<AssistantChannelChatItemViewModel> Items, int Total);

/// <summary>
/// SPM-358: a chat of the person in the list.
/// </summary>
/// <param name="Token">The link token to continue in the chat; it expires with the presented link.</param>
/// <param name="Title">The chat's title.</param>
/// <param name="AgentId">The agent of the chat.</param>
/// <param name="Status">idle, running, waiting-for-approval or failed.</param>
/// <param name="Unread">Whether the chat has messages the person did not read.</param>
public sealed record AssistantChannelChatItemViewModel(string Token, string? Title, string AgentId, string Status, bool Unread);

/// <summary>
/// SPM-358: an agent the person may chat with, and what they may attach to a message to it.
/// </summary>
/// <param name="AgentId">The agent to start a chat with through <c>POST assistant-channel/chats</c>.</param>
/// <param name="DisplayNameKey">The translation key of its name.</param>
/// <param name="AcceptsFiles">Whether the person may attach files; SPM-359: not when the agent's profile chain may not receive them.</param>
/// <param name="AcceptedMediaTypes">The media types the person may attach.</param>
/// <param name="MaxFileBytes">The largest file in bytes; 0 without attachments.</param>
public sealed record AssistantChannelAgentViewModel(
    string AgentId,
    string DisplayNameKey,
    bool AcceptsFiles,
    IReadOnlyList<string> AcceptedMediaTypes,
    int MaxFileBytes);

/// <summary>
/// SPM-332: an approval card: the approval, the proposal hash to answer with and what it does.
/// </summary>
public sealed record AssistantChannelApprovalViewModel(Guid ApprovalId, string ProposalHash, string Summary);

/// <summary>
/// SPM-332: why a delivery started or decided nothing, as a stable code such as
/// <c>assistant-message-duplicate</c> or <c>sample-assistant-channel-delivery-invalid</c>.
/// </summary>
public sealed record AssistantChannelErrorViewModel(string Code);
