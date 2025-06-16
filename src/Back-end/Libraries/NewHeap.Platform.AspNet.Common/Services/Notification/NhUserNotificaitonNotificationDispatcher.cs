using AutoMapper;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.Net.Mail;

namespace NewHeap.Platform.AspNet.Common.Services.Notification;
public partial class NhUserNotificationDeliveryData
{ 
    public NhUserNotificationMutateModel Notification { get; set; } = new NhUserNotificationMutateModel();
}

public partial class NhUserNotificaitonNotificationDispatcher : NhAbstractNotificationDispatcher<NhUserNotificationDeliveryData>
{
    public const string DispatcherIdValue = "UserNotificationDispatcher";
    public override string DispatcherId => DispatcherIdValue;

    protected readonly INhUserNotificationService _userNotificationService;

    public NhUserNotificaitonNotificationDispatcher(
        IRepository<NhNotification> repository, 
        IStringLocalizer<NhUserNotificaitonNotificationDispatcher> localizer, 
        INhDbLogService dbLogService, 
        LogHelperService logHelperService, 
        ValidationService validationService, 
        IMapper mapper,
        ILogger<NhUserNotificaitonNotificationDispatcher> logger,
        INhUserNotificationService userNotificationService
        ) 
        : base(repository, localizer, dbLogService, logHelperService, validationService, mapper, logger)
    {
        _userNotificationService = userNotificationService ?? throw new ArgumentNullException(nameof(userNotificationService), "User notification service cannot be null.");
    }

    protected async override Task<TaskResult> DoDispatchAsync(NhUserNotificationDeliveryData? deliveryData, CancellationToken cancellationToken = default)
    { 
        var taskResult = new TaskResult();

        if (deliveryData?.Notification == null)
        { 
            return taskResult.WithError("InvalidDeliveryData", _localizer["Delivery data is invalid or missing notification."]);
        }

        try
        {
            var createUserNotificationResult = await _userNotificationService.CreateAsync(deliveryData.Notification, cancellationToken: cancellationToken);
            createUserNotificationResult.ApplyToTaskResult(taskResult);

            if (!taskResult.Success)
            {
                return taskResult;
            }
        }
        catch (Exception ex)
        {
            taskResult.AddError("DispatchError", _localizer["Failed to dispatch user notification."]);
            _logger.LogError(ex, "Failed to dispatch user notification.");
        }

        return taskResult;
    }
}
