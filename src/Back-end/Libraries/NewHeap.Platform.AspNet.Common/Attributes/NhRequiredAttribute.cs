using Microsoft.Extensions.Localization;
using NewHeap.Platform.Common.Translations;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Attributes;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public partial class NhRequiredAttribute : RequiredAttribute
{
    protected override ValidationResult IsValid(object value, ValidationContext validationContext)
    {
        var stringLocalizer = validationContext.GetService(typeof(IStringLocalizer<SharedDataAnnotationRecources>)) as IStringLocalizer<SharedDataAnnotationRecources>;
        ErrorMessage = stringLocalizer["The {0} field is required."]?.Value ?? "The {0} field is required.";

        return base.IsValid(value, validationContext);
    }
}
