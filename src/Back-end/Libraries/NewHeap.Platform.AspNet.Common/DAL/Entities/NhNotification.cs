using NewHeap.Platform.AspNet.Common.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public enum NhNotificationPriority
{
    Unknown = 0,
    Low = 10,
    Normal = 20,
    High = 30,
    Critical = 40
}

public enum NhNotificationStatus
{
    // Job persisted and registered in the storage
    Queued = 0,

    //Job dispatched to background job framework
    Scheduled = 10,

    //Framework is executing the job
    Processing = 20,

    //Result is here, it failed
    Failed = 30,

    //Result is here, success
    Succeeded = 40
}

public partial class NhNotification : IdDbEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }
    public DateTimeOffset CreationDateTime { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;

    public Guid? CreatedByUserId { get; set; }

    [Column(TypeName = "smallint")]
    public NhNotificationPriority Priority { get; set; } = NhNotificationPriority.Normal;

    [StringLength(256)]
    public string Title { get; set; } = string.Empty;

    [StringLength(5)]
    public string LanguageCulture { get; set; } = string.Empty;
}
