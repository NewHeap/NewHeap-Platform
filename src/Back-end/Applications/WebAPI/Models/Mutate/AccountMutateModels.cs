using NewHeap.Platform.Common.Attributes;
using System;
using System.ComponentModel.DataAnnotations;

namespace WebAPI.Models.Mutate;

public class LoginAccountMutateModel
{
    [NhRequired]
    [DataType(DataType.EmailAddress)]
    [EmailAddress]
    [Display(Name = "Email address")]
    public string Email { get; set; }

    [NhRequired]
    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    public string Password { get; set; }

    [Display(Name = "Remember me?")]
    public bool RememberMe { get; set; }
}

public class ChangePasswordAccountMutateModel
{
    [NhRequired]
    [DataType(DataType.Password)]
    [Display(Name = "Current password")]
    public string CurrentPassword { get; set; }

    [NhRequired]
    [StringLength(100, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.",
        MinimumLength = 6)]
    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    public string Password { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "Confirm password")]
    [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
    public string ConfirmPassword { get; set; }
}

public class RecoverPasswordAccountMutateModel
{
    [NhRequired]
    [DataType(DataType.EmailAddress)]
    [EmailAddress]
    [Display(Name = "Email address")]
    public string Email { get; set; }

    [NhRequired]
    public string ResetUrl { get; set; }
}

public class ResetPasswordAccountMutateModel
{
    [NhRequired]
    public string Token { get; set; }

    [NhRequired]
    [DataType(DataType.EmailAddress)]
    [EmailAddress]
    [Display(Name = "Email address")]
    public string Email { get; set; }

    [NhRequired]
    [StringLength(100, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.",
        MinimumLength = 6)]
    [DataType(DataType.Password)]
    [Display(Name = "Password")]
    public string Password { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "Confirm password")]
    [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
    public string ConfirmPassword { get; set; }
}

public class LockoutAccountMutateModel
{
    public DateTimeOffset? LockoutEnd { get; set; }
    public DateTimeOffset? LockoutStart { get; set; }
}

public class ChangeCultureAccountMutateModel
{
    [NhRequired]
    [Display(Name = "Culture")]
    [StringLength(5, ErrorMessage = "The {0} must be at least {2} and at max {1} characters long.", MinimumLength = 5)]
    public string Culture { get; set; }
}

public class ChangeActiveDivisionAccountMutateModel
{
    [Display(Name = "Division")]
    public Guid? DivisionId { get; set; }
}