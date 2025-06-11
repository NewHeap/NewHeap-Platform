using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.Linq.Expressions;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Services.Notification;

public static class NhNotificationBuilderExtensions
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="builder"></param>
    /// <param name="delivery"></param>
    /// <param name="priority">Only set if a different priority is required, notification entity will be used if null.</param>
    /// <returns></returns>
    public static NhNotificationBuilder WithEmailDelivery(this NhNotificationBuilder builder, NhEmailDeliveryData delivery, NhNotificationPriority? priority = null)
    {
        var emailDelivery = new NhNotificationDelivery
        {
            DispatcherId = NhEmailNotificationDispatcher.DispatcherIdValue,
            Data = delivery,
            Priority = priority
        };

        return builder.AddDelivery(emailDelivery);
    }
}

public partial class NhNotificationBuilder
{
    protected readonly NhNotification Notification;

    protected NhNotificationBuilder(string name)
    { 
        Notification = new NhNotification
        {
            Priority = NhNotificationPriority.Normal,
            Name = name
        };
    }

    public static NhNotificationBuilder Create(string name)
    {
        return new NhNotificationBuilder(name);
    }

    public NhNotificationBuilder WithName(string name)
    {
        Notification.Name = name;
        return this;
    }

    public NhNotificationBuilder WithPriority(NhNotificationPriority priority)
    {
        Notification.Priority = priority;
        return this;
    }

    public NhNotificationBuilder WithCreatedByUserId(Guid? userId)
    {
        Notification.CreatedByUserId = userId;
        return this;
    }

    public NhNotificationBuilder AddDelivery(NhNotificationDelivery delivery)
    {
        if (delivery == null)
        {
            throw new ArgumentNullException(nameof(delivery), "Delivery cannot be null.");
        }

        Notification.Deliveries.Add(delivery);
        return this;
    }

    public NhNotification Build()
    {
        return Notification;
    }
}

public interface INhNotificationService
{
    Task<TaskResult<NhNotification>> CreateAsync(NhNotification notification, CancellationToken cancellationToken = default);
}

public partial class NhNotificationService : INhNotificationService
{
    protected readonly IRepository<NhNotification> _repository;
    protected readonly IStringLocalizer<NhDivisionService> _localizer;
    protected readonly INhDbLogService _dbLogService;
    protected readonly IMapper _mapper;
    protected readonly ILogger _logger;
    protected readonly LogHelperService _logHelper;
    protected readonly ValidationService _validationService;

    public NhNotificationService(
        IRepository<NhNotification> repository,
        IStringLocalizer<NhDivisionService> localizer,
        INhDbLogService dbLogService,
        LogHelperService logHelperService,
        ValidationService validationService,
        IMapper mapper,
        ILogger<NhNotificationService> logger
        )
    {
        _repository = repository;
        _localizer = localizer;
        _mapper = mapper;
        _dbLogService = dbLogService;
        _logHelper = logHelperService;
        _validationService = validationService;
        _logger = logger;
    }

    protected TaskResult Validate(NhNotification notification)
    {
        var taskResult = new TaskResult();

        if (notification == null)
        {
            taskResult.AddError("Notification cannot be null.");
            return taskResult;
        }

        return taskResult;
    }

    public async Task<TaskResult<NhNotification>> CreateAsync(NhNotification notification, CancellationToken cancellationToken = default)
    {
        var taskResult = new TaskResult<NhNotification>();

        var validationResult = Validate(notification);
        if (!validationResult.Success)
        {
            validationResult.ApplyToTaskResult(taskResult);
            return taskResult;
        }

        notification.CreationDateTime = DateTimeOffset.UtcNow;
        notification.LastModifiedDateTime = DateTimeOffset.UtcNow;

        if (notification.Deliveries?.Any() == true)
        {
            foreach (var delivery in notification.Deliveries)
            {
                delivery.CreationDateTime = DateTimeOffset.UtcNow;
                delivery.LastModifiedDateTime = DateTimeOffset.UtcNow;
                delivery.Status = NotificationDeliveryStatus.Queued;
                delivery.ScheduledAt = DateTimeOffset.UtcNow;
                delivery.SentAt = null;
            }
        }

        using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);

        try
        {
            await _repository.AddAsync(notification, cancellationToken);
            await _repository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while creating the notification: {Message}", ex.Message);
            taskResult.AddError(string.Empty, _localizer["An error occurred while creating the notification."]);
            return taskResult;
        }

        taskResult.Data = notification;

        return taskResult;
    }
}