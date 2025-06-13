using AutoMapper;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
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

public partial class NhEmailNotificationDispatcher : NhAbstractNotificationDispatcher<NhEmailDeliveryData>
{
    public const string DispatcherIdValue = "EmailDispatcher";
    public override string DispatcherId => DispatcherIdValue;

    protected readonly NhMailService _mailService;

    public NhEmailNotificationDispatcher(
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
        _mailService = mailService ?? throw new ArgumentNullException(nameof(mailService), "Mail service cannot be null.");
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

        if (string.IsNullOrWhiteSpace(deliveryData?.FromEmail) || !MailAddress.TryCreate(deliveryData.FromEmail, out _))
        {
            taskResult.AddError(nameof(deliveryData.FromEmail), _localizer["Invalid 'From' email address."]);
        }

        if (string.IsNullOrWhiteSpace(deliveryData?.FromDisplayName))
        {
            taskResult.AddError(nameof(deliveryData.FromDisplayName), _localizer["'From' display name cannot be empty."]);
        }

        return taskResult;
    }

    protected async override Task<TaskResult> DoDispatchAsync(NhEmailDeliveryData? deliveryData, CancellationToken cancellationToken = default)
    { 
        var taskResult = new TaskResult();

        var validateResult = Validate(deliveryData);
        validateResult.ApplyToTaskResult(taskResult);

        if (!taskResult.Success)
        {
            return taskResult;
        }

        try
        {
            var mailMessage = new MailMessage();

            mailMessage.From = new MailAddress(deliveryData!.FromEmail, deliveryData.FromDisplayName);
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
                //if (attachment.Content != null && attachment.Content.Length > 0)
                //{
                // This was suggest by ai but we want to ensure that the memory stream is disposed properly, so we will not use it directly here.
                //    var mailAttachment = new Attachment(new MemoryStream(attachment.Content), attachment.FileName, attachment.ContentType);
                //    mailMessage.Attachments.Add(mailAttachment);
                //}
            }

            await _mailService.SendAsync(mailMessage, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            taskResult.AddError("DispatchError", _localizer["Failed to dispatch email notification."]);
            _logger.LogError(ex, "Failed to dispatch email notification.");
        }

        return taskResult;
    }
}
