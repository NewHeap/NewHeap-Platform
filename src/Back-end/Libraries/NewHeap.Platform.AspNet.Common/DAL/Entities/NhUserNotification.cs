using NewHeap.Platform.AspNet.Common.Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class NhUserNotification : IdDbEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;

    public List<NhUserNotificationMessage> Messages { get; set; } = new List<NhUserNotificationMessage>();

    public Guid UserId { get; set; }

    [StringLength(256)]
    public string LastTitle { get; set; } = string.Empty;
    public string LastMessage { get; set; } = string.Empty;
    public bool IsLastRead { get; set; }
    public bool IsArchived { get; set; }

    /// <summary>
    /// Optional application-defined kind, for example <c>background-operation</c>,
    /// used to choose an icon or filter the inbox.
    /// </summary>
    [StringLength(100)]
    public string? Category { get; set; }

    /// <summary>
    /// Severity of the latest message in the thread.
    /// </summary>
    public NhUserNotificationSeverity Severity { get; set; } = NhUserNotificationSeverity.Information;

    /// <summary>
    /// Optional thread key. New messages with the same key for the same user are
    /// appended to the active (non-archived) notification instead of creating a new one.
    /// </summary>
    [StringLength(200)]
    public string? GroupKey { get; set; }

    public NhUserNotficationData Data { get; set; } = new NhUserNotficationData();
}

public enum NhUserNotificationSeverity
{
    Information = 0,
    Success = 10,
    Warning = 20,
    Error = 30
}

public partial class NhUserNotficationData
{
    public string? Url { get; set; } = null;
    public bool UrlInNewTab { get; set; }
}
