using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Attributes;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Models.Mutate;
public class NhUserNotificationMutateModel
{
    [NhRequired]
    public Guid? UserId { get; set; }

    [NhRequired, StringLength(250)]
    public string? Title { get; set; }

    public string? Message { get; set; }

    public string? Url { get; set; }

    public bool UrlInNewTab { get; set; }

    [StringLength(100)]
    public string? Category { get; set; }

    public NhUserNotificationSeverity Severity { get; set; } = NhUserNotificationSeverity.Information;

    /// <summary>
    /// Optional thread key. <see cref="Services.Notification.INhUserNotificationService.CreateOrAddMessageAsync"/>
    /// appends to the user's active notification with the same key.
    /// </summary>
    [StringLength(200)]
    public string? GroupKey { get; set; }
}

public class NhAddMessageUserNotificationMutateModel
{
    [NhRequired, StringLength(250)]
    public string? Title { get; set; }

    public string? Message { get; set; }

    public NhUserNotificationSeverity Severity { get; set; } = NhUserNotificationSeverity.Information;

    /// <summary>
    /// When set, replaces the notification link so the thread points at the latest target.
    /// </summary>
    public string? Url { get; set; }
}