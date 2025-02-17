using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Models.View;

public partial class ProfileAccountViewModel
{
    public UserViewModel User { get; set; }

    public List<DivisionViewModel> Divisions { get; set; } = new List<DivisionViewModel>();

    public ICollection<ClaimViewModel> Claims { get; set; }

    public ProfileAccountViewModel()
    {
        Claims = new List<ClaimViewModel>();
    }
}
