using System.Globalization;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using SampleProjectManagement.Api.Models;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// Renders the e-mailed two-factor code with the sample's Razor mail layout. Security event
/// messages keep the localized NewHeap texts from the base composer.
/// </summary>
public sealed class SampleTwoFactorMessageComposer : NhTwoFactorMessageComposer
{
    private readonly RazorViewService _razorViewService;
    private readonly TimeProvider _timeProvider;

    public SampleTwoFactorMessageComposer(
        IStringLocalizer<NhTwoFactorMessages> localizer,
        TimeProvider timeProvider,
        RazorViewService razorViewService)
        : base(localizer, timeProvider)
    {
        _razorViewService = razorViewService;
        _timeProvider = timeProvider;
    }

    public override async Task<NhNotification> ComposeEmailCodeAsync(
        NhTwoFactorEmailCodeMessage message,
        CancellationToken cancellationToken = default)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((message.ExpiresAt - _timeProvider.GetUtcNow()).TotalMinutes));
        var signIn = message.Purpose == NhTwoFactorEmailCodePurpose.SignIn;

        var model = new TwoFactorCodeMailViewModel
        {
            Language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
            Title = signIn ? Localizer["Your sign-in code"] : Localizer["Confirm your e-mail address"],
            Introduction = signIn
                ? Localizer[
                    "Your sign-in code is {0}. It expires in {1} minutes. If you did not try to sign in, change your password.",
                    message.Code,
                    minutes]
                : Localizer["Your confirmation code is {0}. It expires in {1} minutes.", message.Code, minutes],
            Code = message.Code,
        };

        return NhNotificationBuilder
            .Create("two-factor-email-code")
            .WithPriority(NhNotificationPriority.High)
            .WithCreatedByUserId(message.UserId)
            .WithEmailDelivery(new NhEmailDeliveryData
            {
                To = [message.Email],
                Subject = model.Title,
                Body = await _razorViewService.RenderViewToStringAsync("Mail/TwoFactorCode", model),
                IsBodyHtml = true,
            })
            .Build();
    }
}
