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
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Services.Notification;

public interface INhNotificationDispatcher
{
    string DispatcherId { get; }

    Task<TaskResult> DispatchAsync(object? deliveryData, CancellationToken cancellationToken = default);
}

public interface INhNotificationDispatcher<in TDeliveryData> : INhNotificationDispatcher
    where TDeliveryData : class, new()
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
    where TDeliveryData : class, new()
{
    public abstract string DispatcherId { get; }
    protected readonly IRepository<NhNotification> _repository;
    protected readonly IStringLocalizer<NhDivisionService> _localizer;
    protected readonly INhDbLogService _dbLogService;
    protected readonly IMapper _mapper;
    protected readonly ILogger _logger;
    protected readonly LogHelperService _logHelper;
    protected readonly ValidationService _validationService;

    public NhAbstractNotificationDispatcher(
        IRepository<NhNotification> repository,
        IStringLocalizer<NhDivisionService> localizer,
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