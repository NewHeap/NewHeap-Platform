using Microsoft.Extensions.Localization;
using NewHeap.Platform.Common.Translations;
using System.ComponentModel.DataAnnotations;

namespace NewHeap.Platform.Common.Attributes;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public partial class NhRequiredAttribute : RequiredAttribute
{
    public bool DisallowAllDefaultValues { get; set; }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        var stringLocalizer =
            (validationContext.GetService(typeof(IStringLocalizer<SharedDataAnnotationRecources>)) as
                IStringLocalizer<SharedDataAnnotationRecources>)!;

        var fieldDisplayNameTranslation = stringLocalizer[validationContext.DisplayName];
        ErrorMessage = stringLocalizer["The {0} field is required.", fieldDisplayNameTranslation]?.Value ?? $"The {validationContext.DisplayName} field is required.";

        if (DisallowAllDefaultValues && value is not null)
        {
            Type type = value.GetType();

            if (type.IsValueType)
            {
                object? defaultValue = Activator.CreateInstance(type);

                if (value.Equals(defaultValue))
                {
                    string error = FormatErrorMessage(validationContext.DisplayName);
                    return new ValidationResult(error);
                }
            }
        }

        return base.IsValid(value, validationContext);
    }
}