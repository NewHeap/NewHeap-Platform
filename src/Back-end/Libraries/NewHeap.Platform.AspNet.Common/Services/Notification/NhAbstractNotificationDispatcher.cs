using NewHeap.Platform.Mapping;
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
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Services.Notification;

/// <summary>
/// Delivery data whose content must not outlive a moment, such as an e-mail that carries a sign-in
/// code. When <see cref="ExpiresAt"/> has a value, the notification processor removes the data from
/// the delivery store as soon as the delivery ends (sent, failed or cancelled), cancels the delivery
/// instead of dispatching it once that moment has passed, and removes the data of a delivery that is
/// still waiting at that moment in its next cleanup pass. Without a value the data follows
/// <see cref="NhNotificationSettings.ProcessingRetentionPeriod"/>.
/// </summary>
/// <remarks>
/// The processor recognizes the data through the delivery data type of the registered
/// <see cref="INhNotificationDispatcher{TDeliveryData}"/>, so implement this interface on that type.
/// </remarks>
public interface INhExpiringDeliveryData
{
    /// <summary>When the content stops being valid; <see langword="null"/> keeps it for the retention period.</summary>
    DateTimeOffset? ExpiresAt { get; set; }
}

public interface INhNotificationDispatcher
{
    string DispatcherId { get; }

    Task<TaskResult> DispatchAsync(object? deliveryData, CancellationToken cancellationToken = default);
}

public interface INhNotificationDispatcher<in TDeliveryData> : INhNotificationDispatcher
    where TDeliveryData : class
{
    async Task<TaskResult> INhNotificationDispatcher.DispatchAsync(object? deliveryData, CancellationToken cancellationToken)
    {
        if (deliveryData is not null)
        {
            if (deliveryData is not TDeliveryData && deliveryData is JObject)
            {
                var parsedData = ((JObject)deliveryData).ToObject<TDeliveryData>();
                return await DispatchAsync(parsedData, cancellationToken);
            }
        }

        return await DispatchAsync((TDeliveryData?)deliveryData, cancellationToken);
    }

    Task<TaskResult> DispatchAsync(TDeliveryData? deliveryData, CancellationToken cancellationToken = default);
}

public abstract partial class NhAbstractNotificationDispatcher<TDeliveryData> : INhNotificationDispatcher<TDeliveryData>
    where TDeliveryData : class
{
    public abstract string DispatcherId { get; }
    protected readonly IRepository<NhNotification> _repository;
    protected readonly IStringLocalizer<NhAbstractNotificationDispatcher<TDeliveryData>> _localizer;
    protected readonly INhDbLogService _dbLogService;
    protected readonly IMapper _mapper;
    protected readonly ILogger _logger;
    protected readonly LogHelperService _logHelper;
    protected readonly ValidationService _validationService;

    public NhAbstractNotificationDispatcher(
        IRepository<NhNotification> repository,
        IStringLocalizer<NhAbstractNotificationDispatcher<TDeliveryData>> localizer,
        INhDbLogService dbLogService,
        LogHelperService logHelperService,
        ValidationService validationService,
        IMapper mapper,
        ILogger<NhAbstractNotificationDispatcher<TDeliveryData>> logger
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

    public async Task<TaskResult> DispatchAsync(TDeliveryData? deliveryData, CancellationToken cancellationToken = default)
    { 
        var taskResult = new TaskResult();

        // Might want to do some before and after actions here later.
        var doTaskResult = await DoDispatchAsync(deliveryData, cancellationToken);
        doTaskResult.ApplyToTaskResult(taskResult);

        return taskResult;
    }

    protected abstract Task<TaskResult> DoDispatchAsync(TDeliveryData? deliveryData, CancellationToken cancellationToken = default);
}