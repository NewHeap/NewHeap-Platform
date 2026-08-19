using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Models.View;

public class NhOverviewUserNotificationViewModel
{
    public int TotalCount { get; set; } = 0;
    public int UnreadCount { get; set; } = 0;
    public DateTimeOffset? LastNotificationDate { get; set; }
}

public class NhUserNotificationViewModel
{
    [Filterable, Orderable]
    public Guid Id { get; set; }

    [Filterable, Orderable]
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;

    [Filterable, Orderable]
    public DateTimeOffset LastModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;

    [Filterable, Orderable, Searchable]
    public List<NhUserNotificationMessageViewModel> Messages { get; set; } = new List<NhUserNotificationMessageViewModel>();

    [Filterable, Orderable]
    public string LastTitle { get; set; } = string.Empty;
    public string LastMessage { get; set; } = string.Empty;

    [Filterable, Orderable]
    public bool IsLastRead { get; set; }

    public NhUserNotficationData Data { get; set; } = new NhUserNotficationData();
}

public class NhUserNotificationMessageViewModel
{
    [Filterable, Orderable]
    public Guid Id { get; set; }

    [Filterable, Orderable]
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;

    [Filterable, Orderable]
    public DateTimeOffset LastModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;

    [Filterable, Orderable, Searchable]
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    [Filterable, Orderable]
    public Guid UserNotificationId { get; set; }
}


public partial class NhUserNotificationCollectionRequestModel : CollectionRequestModel
{

}