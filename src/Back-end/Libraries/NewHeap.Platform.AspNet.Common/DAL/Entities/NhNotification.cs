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
    public string Name { get; set; } = string.Empty;

    public List<NhNotificationDelivery> Deliveries { get; set; } = new List<NhNotificationDelivery>();
}
