using System.ComponentModel.DataAnnotations;

namespace NewHeap.Platform.AspNet.Common.Models;

public class ChangeActiveDivisionAccountModel
{
    [Display(Name = "Division")]
    public Guid? DivisionId { get; set; }
}