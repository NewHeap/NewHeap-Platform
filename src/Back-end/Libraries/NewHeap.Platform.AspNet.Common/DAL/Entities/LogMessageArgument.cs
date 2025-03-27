using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

/// <summary>
///     Note: Immutable rows
/// </summary>
public partial class LogMessageArgument
{
    public LogMessageArgument()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
        Index = 0;
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public DateTimeOffset CreationDateTime { get; set; }

    public Guid LogId { get; set; }

    public int Index { get; set; }

    public string? Value { get; set; }
}