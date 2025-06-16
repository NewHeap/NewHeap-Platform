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
}

public partial class NhUserNotificationCollectionRequestModel : CollectionRequestModel
{

}