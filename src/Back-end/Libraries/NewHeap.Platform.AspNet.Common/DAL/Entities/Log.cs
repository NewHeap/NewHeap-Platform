using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public enum LogSource
{
    Unknown = 0,
    Internal = 1000,
    External = 2000
}

public enum LogType
{
    Unknown = 0,
    Information = 1000,
    Warning = 2000,
    Error = 3000
}

public enum LogAction
{
    Unknown = 0,
    Read = 1000,
    Create = 2000,
    Update = 3000,
    Delete = 4000
}

/// <summary>
///     Note: Immutable rows
/// </summary>
public partial class Log
{
    public Log()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
        Source = LogSource.Unknown;
        Type = LogType.Unknown;
        Action = LogAction.Unknown;
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public DateTimeOffset CreationDateTime { get; set; }

    [StringLength(50)]
    public string Tag { get; set; }

    /// <summary>
    ///     The object type
    /// </summary>
    [StringLength(100)]
    public string ObjectType { get; set; }

    /// <summary>
    ///     The object type including namespace
    /// </summary>
    [StringLength(250)]
    public string ObjectTypeFull { get; set; }

    /// <summary>
    ///     The object id
    /// </summary>
    [StringLength(64)]
    public string ObjectId { get; set; }

    public string Message { get; set; }

    public LogType Type { get; set; }

    public LogAction Action { get; set; }

    public LogSource Source { get; set; }

    [Display(Name = "User")]
    public Guid? UserId { get; set; }

    public User User { get; set; }

    public List<LogFile> Files { get; set; }

    public List<LogMessageArgument> MessageArguments { get; set; }

    public List<LogMessageTranslated> MessageTranslateds { get; set; }

    public Guid? DivisionId { get; set; }

    public Division Division { get; set; }
}