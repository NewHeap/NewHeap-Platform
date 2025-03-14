using NewHeap.Platform.AspNet.Common.Models;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class Division : IdDbEntity
{
    public Division()
    {
        CreationDateTime = DateTimeOffset.UtcNow;
        LastModifiedDateTime = DateTimeOffset.UtcNow;
    }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public DateTimeOffset CreationDateTime { get; set; }

    public DateTimeOffset LastModifiedDateTime { get; set; }

    [StringLength(100)]
    public string Name { get; set; } = "";

    [StringLength(255)]
    public string Description { get; set; } = "";

    /// <summary>
    ///     The TimeZone id returned by the TimeZoneInfo.Id property.
    /// </summary>
    [StringLength(50)]
    public string TimeZoneId { get; set; } = "";

    public bool UserSelectAllowed { get; set; }

    public ICollection<DivisionUser> DivisionUsers { get; set; } = new List<DivisionUser>();
}