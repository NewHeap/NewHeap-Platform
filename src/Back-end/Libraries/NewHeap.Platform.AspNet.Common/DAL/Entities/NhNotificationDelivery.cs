using NewHeap.Platform.AspNet.Common.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class NhNotificationDelivery : IdDbEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ScheduledAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSendAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }

    public int AttemptCount { get; set; } = 0;
    public string? LastFailedMessage { get; set; } = null;

    public Guid NotificationId { get; set; }
    public NhNotification? Notification { get; set; }
    public NotificationDeliveryStatus Status { get; set; } = NotificationDeliveryStatus.Queued;

    [Column(TypeName = "smallint")]
    public NhNotificationPriority? Priority { get; set; } = null;

    [StringLength(50)]
    public string DispatcherId { get; set; } = string.Empty;

    [Column(TypeName = "nvarchar(MAX)")]
    public object? Data { get; set; } = null;

    public bool IsCleaned { get; set; } = false;
}

public enum NotificationDeliveryStatus
{
    /// <summary>
    /// Persisted and registered in the storage, but not yet scheduled.
    /// </summary>
    Queued = 0,

    /// <summary>
    /// Is currently being processed by the dispatcher. (In-flight)
    /// </summary>
    Processing = 10,

    /// <summary>
    /// Represents a state where the operation has failed and no retry will be attempted.
    /// </summary>
    Failed = 20,

    /// <summary>
    /// Represents a status indicating that the operation succeeded.
    /// </summary>
    Succeeded = 30,

    /// <summary>
    /// Represents the status of an operation that has been cancelled.
    /// </summary>
    Cancelled = 90
}