using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

/// <summary>
///     Note: Immutable rows
/// </summary>
public partial class LogFile
{
    public LogFile()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public DateTimeOffset CreationDateTime { get; set; }

    public Guid LogId { get; set; }

    public Log Log { get; set; } = null!;

    [StringLength(254)]
    public required string OriginalFileName { get; set; }

    /// <summary>
    ///     Relative path to a related file
    /// </summary>
    public string FilePath { get; set; } = "";
}