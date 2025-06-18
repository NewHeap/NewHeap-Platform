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

    [Column(TypeName = "nvarchar(MAX)")]
    public NhUserNotficationData Data { get; set; } = new NhUserNotficationData();
}

public partial class NhUserNotficationData
{
    public string? Url { get; set; } = null;
    public bool UrlInNewTab { get; set; }
}