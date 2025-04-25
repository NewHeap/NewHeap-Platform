using NewHeap.Platform.AspNet.Common.Attributes;
using System.ComponentModel.DataAnnotations;

namespace NewHeap.Platform.AspNet.Common.Models.Mutate;

public class NhWithoutCurrentPasswordChangePasswordUserMutateModel
{
    [NhRequired]
    [StringLength(100, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.", MinimumLength = 6)]
    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    public string? Password { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "Confirm password")]
    [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
    public string? ConfirmPassword { get; set; }
}

public class NhChangePasswordUserMutateModel
{
    [NhRequired]
    [DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string? CurrentPassword { get; set; }

    [NhRequired]
    [StringLength(100, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.", MinimumLength = 6)]
    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    public string? Password { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "Confirm password")]
    [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
    public string? ConfirmPassword { get; set; }
}

public class NhRecoverPasswordUserMutateModel
{
    [NhRequired]
    [DataType(DataType.EmailAddress), EmailAddress]
    [Display(Name = "Email address")]
    public string? Email { get; set; }

    [NhRequired]
    public string? ResetUrl { get; set; }
}

public class NhResetPasswordUserMutateModel
{
    [NhRequired]
    public string? Token { get; set; }

    [NhRequired]
    [DataType(DataType.EmailAddress), EmailAddress]
    [Display(Name = "Email address")]
    public string? Email { get; set; }

    [NhRequired]
    [StringLength(100, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.", MinimumLength = 6)]
    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    public string? Password { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "Confirm password")]
    [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
    public string? ConfirmPassword { get; set; }
}

public class LockoutUserMutateModel
{
    public DateTimeOffset? LockoutEnd { get; set; }
    public DateTimeOffset? LockoutStart { get; set; }
}