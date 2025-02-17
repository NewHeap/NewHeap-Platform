using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

/// <summary>
/// Note: Immutable rows
/// </summary>
public partial class LogFile
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public DateTimeOffset CreationDateTime { get; set; }

    public Guid LogId { get; set; }

    public Log Log { get; set; }

    [StringLength(254)]
    public string OriginalFileName { get; set; }

    /// <summary>
    /// Relative path to a related file
    /// </summary>
    public string FilePath { get; set; }

    public LogFile()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
    } 
}
