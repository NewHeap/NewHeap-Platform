using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Models
{
    public class ChangeActiveDivisionAccountModel
    {
        [Display(Name = "Division")]
        public Guid? DivisionId { get; set; }
    }
}
