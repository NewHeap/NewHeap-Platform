using AutoMapper;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.Net.Mail;

namespace NewHeap.Platform.AspNet.Common.Services.Notification;

public partial class NhEmailDeliveryData
{
    public partial class Attachment
    {
        public string FileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public byte[] Content { get; set; } = Array.Empty<byte>();
    }

    public string FromEmail { get; init; } = string.Empty;
    public string FromDisplayName { get; init; } = string.Empty;

    public string Subject { get; set; } = "";
    public string? Body { get; set; }
    public bool IsBodyHtml { get; set; } = true;
    public List<string> To { get; set; } = new List<string>();
    public List<string> CC { get; set; } = new List<string>();
    public List<string> BCC { get; set; } = new List<string>();
    public List<Attachment> Attachments { get; set; } = new List<Attachment>();
}

public class NhEmailNotificationSettings
{
    public bool AllowDefaultFromAddress { get; set; }

    public string? DefaultFromAddress { get; set; }

    public string? DefaultFromName { get; set; }
}

public partial class NhEmailNotificationDispatcher : NhAbstractNotificationDispatcher<NhEmailDeliveryData>
{
    public const string DispatcherIdValue = "EmailDispatcher";
    public override string DispatcherId => DispatcherIdValue;

    private readonly IOptions<NhEmailNotificationSettings> _settings;
    protected readonly NhMailService _mailService;

    public NhEmailNotificationDispatcher(
        IOptions<NhEmailNotificationSettings> settings,
        IRepository<NhNotification> repository,
        IStringLocalizer<NhEmailNotificationDispatcher> localizer,
        INhDbLogService dbLogService,
        LogHelperService logHelperService,
        ValidationService validationService,
        IMapper mapper,
        ILogger<NhEmailNotificationDispatcher> logger,
        NhMailService mailService
    )
        : base(repository, localizer, dbLogService, logHelperService, validationService, mapper, logger)
    {
        _settings = settings;
        _mailService = mailService ??
                       throw new ArgumentNullException(nameof(mailService), "Mail service cannot be null.");
    }

    protected TaskResult Validate(NhEmailDeliveryData? deliveryData)
    {
        var taskResult = new TaskResult();

        if (deliveryData == null)
        {
            taskResult.AddError(string.Empty, _localizer["Delivery data cannot be null."]);
            return taskResult;
        }

        if (string.IsNullOrWhiteSpace(deliveryData?.Subject))
        {
            taskResult.AddError(nameof(deliveryData.Subject), _localizer["Email subject cannot be empty."]);
        }

        if (string.IsNullOrWhiteSpace(deliveryData?.Body))
        {
            taskResult.AddError(nameof(deliveryData.Body), _localizer["Email body cannot be empty."]);
        }

        if (deliveryData?.To?.Any() != true)
        {
            taskResult.AddError(nameof(deliveryData.To), _localizer["At least one recipient is required."]);
        }

        if (!_settings.Value.AllowDefaultFromAddress || !string.IsNullOrWhiteSpace(deliveryData?.FromEmail))
        {
            if (string.IsNullOrWhiteSpace(deliveryData?.FromEmail) ||
                !MailAddress.TryCreate(deliveryData.FromEmail, out _))
            {
                taskResult.AddError(nameof(deliveryData.FromEmail), _localizer["Invalid 'From' email address."]);
            }
        }
        else if (
            _settings.Value.AllowDefaultFromAddress
            && string.IsNullOrWhiteSpace(deliveryData?.FromEmail)
            &&
            (
                string.IsNullOrWhiteSpace(_settings.Value.DefaultFromAddress)
                || !MailAddress.TryCreate(_settings.Value.DefaultFromAddress, out _)
            )
        )
        {
            taskResult.AddError(nameof(deliveryData.FromEmail), _localizer["Default 'From' email address is invalid."]);
        }

        if (!_settings.Value.AllowDefaultFromAddress && string.IsNullOrWhiteSpace(deliveryData?.FromDisplayName))
        {
            taskResult.AddError(nameof(deliveryData.FromDisplayName),
                _localizer["'From' display name cannot be empty."]);
        }

        return taskResult;
    }

    protected async override Task<TaskResult> DoDispatchAsync(NhEmailDeliveryData? deliveryData,
        CancellationToken cancellationToken = default)
    {
        var taskResult = new TaskResult();

        var validateResult = Validate(deliveryData);
        validateResult.ApplyToTaskResult(taskResult);

        if (!taskResult.Success)
        {
            return taskResult;
        }

        var disposables = new List<IDisposable>();

        try
        {
            var mailMessage = new MailMessage();

            if (!string.IsNullOrWhiteSpace(deliveryData!.FromEmail))
            {
                mailMessage.From = new MailAddress(deliveryData!.FromEmail, deliveryData.FromDisplayName);
            }

            if (mailMessage.From == null)
            {
                var displayName = string.IsNullOrWhiteSpace(_settings.Value.DefaultFromName)
                    ? _settings.Value.DefaultFromAddress
                    : _settings.Value.DefaultFromName;
                mailMessage.From = new MailAddress(_settings.Value.DefaultFromAddress!, displayName);
            }

            mailMessage.Subject = deliveryData.Subject;
            mailMessage.Body = deliveryData.Body;
            mailMessage.IsBodyHtml = deliveryData.IsBodyHtml;

            foreach (var to in deliveryData.To)
            {
                mailMessage.To.Add(new MailAddress(to));
            }

            foreach (var cc in deliveryData.CC)
            {
                mailMessage.CC.Add(new MailAddress(cc));
            }

            foreach (var bcc in deliveryData.BCC)
            {
                mailMessage.Bcc.Add(new MailAddress(bcc));
            }

            foreach (var attachment in deliveryData.Attachments)
            {
                if (attachment.Content != null && attachment.Content.Length > 0)
                {
                    var mailAttachment = new Attachment(new MemoryStream(attachment.Content), attachment.FileName,
                        attachment.ContentType);
                    disposables.Add(mailAttachment); // Collect to be disposed after sending
                    mailMessage.Attachments.Add(mailAttachment);
                }
            }

            await _mailService.SendAsync(mailMessage, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            taskResult.AddError("DispatchError", _localizer["Failed to dispatch email notification."]);
            _logger.LogError(ex, "Failed to dispatch email notification.");
        }
        finally
        {
            foreach (var disposable in disposables)
            {
                try
                {
                    disposable.Dispose();
                }
                catch
                {
                    // F in chat
                }
            }
        }

        return taskResult;
    }
}