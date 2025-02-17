using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NewHeap.Platform.AspNet.Common.DAL.Entities;

public partial class DivisionUserRole
{
    public Guid DivisionUserId { get; set; }
    public DivisionUser DivisionUser { get; set; }
    public Guid DivisionRoleId { get; set; }
    public DivisionRole DivisionRole { get; set; }
}
