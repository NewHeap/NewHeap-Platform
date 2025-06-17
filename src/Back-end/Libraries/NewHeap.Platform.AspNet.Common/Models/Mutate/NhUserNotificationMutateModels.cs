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
}

public class NhAddMessageUserNotificationMutateModel
{
    [NhRequired, StringLength(250)]
    public string? Title { get; set; }

    public string? Message { get; set; }
}