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

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public class NhGreaterThanAttribute : ValidationAttribute
{
    public double Minimum { get; }

    /// <summary>
    /// If true, null values are considered valid. Default: false.
    /// </summary>
    public bool AllowNull { get; set; } = false;

    public NhGreaterThanAttribute(double minimum)
    {
        Minimum = minimum;
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        var stringLocalizer =
          (validationContext.GetService(typeof(IStringLocalizer<SharedDataAnnotationRecources>)) as
              IStringLocalizer<SharedDataAnnotationRecources>)!;

        var fieldDisplayNameTranslation = stringLocalizer[validationContext.DisplayName];
        ErrorMessage = stringLocalizer["{0} should be greater than {1}.", fieldDisplayNameTranslation, Minimum]?.Value ?? $"{validationContext.DisplayName} should be greater than {Minimum}.";

        // Null handling
        if (value == null)
        {
            if (AllowNull)
                return ValidationResult.Success;

            var msg = FormatErrorMessage(ErrorMessage);
            return new ValidationResult(msg);
        }

        try
        {
            double numericValue = Convert.ToDouble(value);
            if (numericValue > Minimum)
                return ValidationResult.Success;

            string msg = FormatErrorMessage(ErrorMessage);
            return new ValidationResult(msg);
        }
        catch (Exception)
        {
            string msg = FormatErrorMessage($"The {validationContext.DisplayName} field is not a numeric type.");
            return new ValidationResult(msg);
        }
    }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public class NhLessThanAttribute : ValidationAttribute
{
    public double Maximum { get; }

    /// <summary>
    /// If true, null values are considered valid. Default: false.
    /// </summary>
    public bool AllowNull { get; set; } = false;

    public NhLessThanAttribute(double maximum)
    {
        Maximum = maximum;
    }

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        var stringLocalizer =
            (validationContext.GetService(typeof(IStringLocalizer<SharedDataAnnotationRecources>)) as
                IStringLocalizer<SharedDataAnnotationRecources>)!;

        // Vertaal de veldnaam en de foutboodschap
        var fieldDisplayNameTranslation = stringLocalizer[validationContext.DisplayName];
        ErrorMessage = stringLocalizer["{0} should be less than {1}.", fieldDisplayNameTranslation, Maximum]?.Value
                       ?? $"{validationContext.DisplayName} should be less than {Maximum}.";

        // Null handling
        if (value == null)
        {
            if (AllowNull)
                return ValidationResult.Success;

            var msg = FormatErrorMessage(ErrorMessage);
            return new ValidationResult(msg);
        }

        try
        {
            double numericValue = Convert.ToDouble(value);
            if (numericValue < Maximum)
                return ValidationResult.Success;

            string msg = FormatErrorMessage(ErrorMessage);
            return new ValidationResult(msg);
        }
        catch (Exception)
        {
            string msg = FormatErrorMessage($"The {validationContext.DisplayName} field is not a numeric type.");
            return new ValidationResult(msg);
        }
    }
}