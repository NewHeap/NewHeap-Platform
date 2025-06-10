using AutoMapper;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Services.Notification;
public partial class NhEmailDeliveryData
{
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public List<string> To { get; set; } = new List<string>();
    public List<string> CC { get; set; } = new List<string>();
    public List<string> BCC { get; set; } = new List<string>();
}

public partial class NhEmailNotificationDispatcher : NhAbstractNotificationDispatcher<NhEmailDeliveryData>
{
    public NhEmailNotificationDispatcher(
        IRepository<NhNotification> repository, 
        IStringLocalizer<NhDivisionService> localizer, 
        INhDbLogService dbLogService, 
        LogHelperService logHelperService, 
        ValidationService validationService, 
        IMapper mapper
        ) 
        : base(repository, localizer, dbLogService, logHelperService, validationService, mapper)
    {
    }
}
