using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class LogMessageTranslated
{
    public LogMessageTranslated()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public DateTimeOffset CreationDateTime { get; set; }

    public Guid LogId { get; set; }

    public Log Log { get; set; }

    /// <summary>
    ///     Ëxample: en-US
    /// </summary>
    [StringLength(5)]
    public string Culture { get; set; }

    public string Message { get; set; }
}