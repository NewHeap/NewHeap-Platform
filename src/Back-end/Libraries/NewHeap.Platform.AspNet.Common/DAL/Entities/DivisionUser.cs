using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class DivisionUser
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User User { get; set; }

    public Guid DivisionId { get; set; }

    public Division Division { get; set; }

    public DateTimeOffset? LockOutStartDateTime { get; set; }
    public DateTimeOffset? LockOutEndDateTime { get; set; }

    public ICollection<DivisionUserRole> DivisionUserRoles { get; set; } = new List<DivisionUserRole>();

    public DivisionUser()
    {
    } 
}
