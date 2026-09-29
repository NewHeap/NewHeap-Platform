using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using System.Text;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Account events that a security notification reports to the user.
/// </summary>
public enum NhTwoFactorSecurityEvent
{
    Enabled = 0,
    Disabled = 10,
    AuthenticatorChanged = 20,
    RecoveryCodesRegenerated = 30,
    RecoveryCodeUsed = 40,
    EmailFactorEnabled = 50,
    LockedOut = 60,
    ResetByAdministrator = 70,
    DevicesForgotten = 80,
    EnrollmentReminder = 90,
    PasskeyAdded = 100,
    PasskeyRemoved = 110,
}

/// <summary>
/// Why a code is e-mailed.
/// </summary>
public enum NhTwoFactorEmailCodePurpose
{
    /// <summary>The code completes a sign-in challenge.</summary>
    SignIn = 0,

    /// <summary>The code confirms the e-mail address as a second factor.</summary>
    Confirmation = 10,
}

/// <summary>
/// Content for an e-mailed code. The code is only valid together with the pending challenge
/// or enrollment it was requested for.
/// </summary>
public sealed class NhTwoFactorEmailCodeMessage
{
    public required Guid UserId { get; init; }
    public required string Email { get; init; }
    public required string Code { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required NhTwoFactorEmailCodePurpose Purpose { get; init; }
}

/// <summary>
/// Content for a security notification.
/// </summary>
public sealed class NhTwoFactorSecurityEventMessage
{
    public required Guid UserId { get; init; }
    public string? Email { get; init; }
    public required NhTwoFactorSecurityEvent Event { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Recovery codes left after <see cref="NhTwoFactorSecurityEvent.RecoveryCodeUsed"/>.</summary>
    public int? RecoveryCodesLeft { get; init; }

    /// <summary>Whether the notification should be e-mailed.</summary>
    public bool IncludeEmail { get; init; }

    /// <summary>Whether the notification should appear in the user's notification inbox.</summary>
    public bool IncludeInApp { get; init; }
}

/// <summary>
/// Builds the notifications for e-mailed codes and security events. Replace it through
/// <see cref="NhTwoFactorBuilder.UseMessageComposer{TComposer}"/> for branded templates.
/// </summary>
public interface INhTwoFactorMessageComposer
{
    /// <summary>Builds the notification that e-mails a code. It must only use e-mail delivery.</summary>
    Task<NhNotification> ComposeEmailCodeAsync(
        NhTwoFactorEmailCodeMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a security notification, or returns <see langword="null"/> to skip the event.
    /// </summary>
    Task<NhNotification?> ComposeSecurityEventAsync(
        NhTwoFactorSecurityEventMessage message,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resource marker for the default two-factor message texts.
/// </summary>
public sealed class NhTwoFactorMessages
{
    private NhTwoFactorMessages()
    {
    }
}

/// <summary>
/// Default plain-text messages, localized through <see cref="IStringLocalizer{T}"/> with
/// English fallback texts. E-mail deliveries use the default sender configured for the
/// NewHeap e-mail dispatcher.
/// </summary>
public class NhTwoFactorMessageComposer : INhTwoFactorMessageComposer
{
    /// <summary>Category of security notifications in the notification inbox.</summary>
    public const string SecurityCategory = "security";

    protected readonly IStringLocalizer<NhTwoFactorMessages> Localizer;
    private readonly TimeProvider _timeProvider;

    public NhTwoFactorMessageComposer(IStringLocalizer<NhTwoFactorMessages> localizer, TimeProvider timeProvider)
    {
        Localizer = localizer;
        _timeProvider = timeProvider;
    }

    public virtual Task<NhNotification> ComposeEmailCodeAsync(
        NhTwoFactorEmailCodeMessage message,
        CancellationToken cancellationToken = default)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((message.ExpiresAt - _timeProvider.GetUtcNow()).TotalMinutes));

        string subject;
        string body;
        if (message.Purpose == NhTwoFactorEmailCodePurpose.SignIn)
        {
            subject = Localizer["Your sign-in code"];
            body = Localizer[
                "Your sign-in code is {0}. It expires in {1} minutes. If you did not try to sign in, change your password.",
                message.Code,
                minutes];
        }
        else
        {
            subject = Localizer["Confirm your e-mail address"];
            body = Localizer[
                "Your confirmation code is {0}. It expires in {1} minutes.",
                message.Code,
                minutes];
        }

        var notification = NhNotificationBuilder
            .Create("two-factor-email-code")
            .WithPriority(NhNotificationPriority.High)
            .WithCreatedByUserId(message.UserId)
            .WithEmailDelivery(new NhEmailDeliveryData
            {
                To = [message.Email],
                Subject = subject,
                Body = body,
                IsBodyHtml = false,
            })
            .Build();

        return Task.FromResult(notification);
    }

    public virtual Task<NhNotification?> ComposeSecurityEventAsync(
        NhTwoFactorSecurityEventMessage message,
        CancellationToken cancellationToken = default)
    {
        var includeEmail = message.IncludeEmail && !string.IsNullOrWhiteSpace(message.Email);
        if (!includeEmail && !message.IncludeInApp)
        {
            return Task.FromResult<NhNotification?>(null);
        }

        var description = Describe(message);
        var builder = NhNotificationBuilder
            .Create(NotificationName(message.Event))
            .WithPriority(description.Severity >= NhUserNotificationSeverity.Warning
                ? NhNotificationPriority.High
                : NhNotificationPriority.Normal)
            .WithCreatedByUserId(message.UserId);

        if (message.IncludeInApp)
        {
            builder.WithUserNotificationDelivery(new NhUserNotificationDeliveryData
            {
                Notification = new NhUserNotificationMutateModel
                {
                    UserId = message.UserId,
                    Title = description.Title,
                    Message = description.Message,
                    Category = SecurityCategory,
                    Severity = description.Severity,
                    GroupKey = "security:" + message.UserId.ToString("N"),
                },
            });
        }

        if (includeEmail)
        {
            builder.WithEmailDelivery(new NhEmailDeliveryData
            {
                To = [message.Email!],
                Subject = description.Title,
                Body = description.Message,
                IsBodyHtml = false,
            });
        }

        return Task.FromResult<NhNotification?>(builder.Build());
    }

    /// <summary>
    /// Returns the notification name for a security event, for example
    /// <c>two-factor-recovery-code-used</c>.
    /// </summary>
    public static string NotificationName(NhTwoFactorSecurityEvent securityEvent)
    {
        var name = securityEvent.ToString();
        var builder = new StringBuilder("two-factor", name.Length + 16);
        foreach (var character in name)
        {
            if (char.IsUpper(character))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Returns the title, text and severity for a security event.
    /// </summary>
    protected virtual NhTwoFactorMessageDescription Describe(NhTwoFactorSecurityEventMessage message)
    {
        return message.Event switch
        {
            NhTwoFactorSecurityEvent.Enabled => new(
                Localizer["Two-factor authentication enabled"],
                Localizer["Two-factor authentication was enabled on your account. Your other sessions were signed out."],
                NhUserNotificationSeverity.Success),
            NhTwoFactorSecurityEvent.Disabled => new(
                Localizer["Two-factor authentication disabled"],
                Localizer["Two-factor authentication was disabled on your account. If this was not you, change your password now."],
                NhUserNotificationSeverity.Warning),
            NhTwoFactorSecurityEvent.AuthenticatorChanged => new(
                Localizer["Authenticator app changed"],
                Localizer["A new authenticator app was linked to your account. If this was not you, change your password now."],
                NhUserNotificationSeverity.Warning),
            NhTwoFactorSecurityEvent.RecoveryCodesRegenerated => new(
                Localizer["New recovery codes"],
                Localizer["New recovery codes were created for your account. The previous codes no longer work."],
                NhUserNotificationSeverity.Information),
            NhTwoFactorSecurityEvent.RecoveryCodeUsed => new(
                Localizer["Recovery code used"],
                Localizer["A recovery code was used to sign in to your account. {0} codes are left.", message.RecoveryCodesLeft ?? 0],
                NhUserNotificationSeverity.Warning),
            NhTwoFactorSecurityEvent.EmailFactorEnabled => new(
                Localizer["E-mail codes enabled"],
                Localizer["Sign-in codes can now be sent to this e-mail address."],
                NhUserNotificationSeverity.Information),
            NhTwoFactorSecurityEvent.LockedOut => new(
                Localizer["Sign-in blocked"],
                Localizer["Your account was temporarily locked after too many invalid codes. If this was not you, change your password."],
                NhUserNotificationSeverity.Error),
            NhTwoFactorSecurityEvent.ResetByAdministrator => new(
                Localizer["Two-factor authentication reset"],
                Localizer["An administrator reset two-factor authentication on your account. Set it up again when you next sign in."],
                NhUserNotificationSeverity.Warning),
            NhTwoFactorSecurityEvent.DevicesForgotten => new(
                Localizer["Remembered devices forgotten"],
                Localizer["Every device will ask for your second factor again."],
                NhUserNotificationSeverity.Information),
            NhTwoFactorSecurityEvent.EnrollmentReminder => new(
                Localizer["Set up two-factor authentication"],
                Localizer["Your organization requires two-factor authentication for your account. You will set it up when you next sign in."],
                NhUserNotificationSeverity.Warning),
            NhTwoFactorSecurityEvent.PasskeyAdded => new(
                Localizer["Passkey added"],
                Localizer["A passkey was added to your account. If this was not you, change your password now."],
                NhUserNotificationSeverity.Warning),
            NhTwoFactorSecurityEvent.PasskeyRemoved => new(
                Localizer["Passkey removed"],
                Localizer["A passkey was removed from your account."],
                NhUserNotificationSeverity.Information),
            _ => throw new ArgumentOutOfRangeException(nameof(message), message.Event, "Unknown two-factor security event."),
        };
    }
}

/// <summary>
/// Title, text and severity of a security notification.
/// </summary>
public sealed record NhTwoFactorMessageDescription(string Title, string Message, NhUserNotificationSeverity Severity);
