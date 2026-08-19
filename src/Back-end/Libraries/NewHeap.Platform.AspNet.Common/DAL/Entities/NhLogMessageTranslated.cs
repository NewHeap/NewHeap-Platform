using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class NhLogMessageTranslated
{
    public NhLogMessageTranslated()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public DateTimeOffset CreationDateTime { get; set; }

    public Guid LogId { get; set; }

    /// <summary>
    ///     Ëxample: en-US
    /// </summary>
    [StringLength(5)]
    public string Culture { get; set; } = null!;

    public string Message { get; set; } = null!;
}