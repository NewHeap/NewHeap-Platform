using NewHeap.Platform.Common.Attributes;
using System.ComponentModel.DataAnnotations;

namespace WebAPI.Models.Mutate;

public class LocalizationTestModel
{
    [NhRequired]
    [Display(Name = "Name")]
    public string? Name { get; set; }
}
