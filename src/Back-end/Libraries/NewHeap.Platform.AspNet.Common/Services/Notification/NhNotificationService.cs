using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
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


public partial class NhNotificationService 
{
    protected readonly IRepository<NhNotification> _repository;
    protected readonly IStringLocalizer<NhDivisionService> _localizer;
    protected readonly INhDbLogService _dbLogService;
    protected readonly IMapper _mapper;
    protected readonly LogHelperService _logHelper;
    protected readonly ValidationService _validationService;

    public NhNotificationService(
        IRepository<NhNotification> repository,
        IStringLocalizer<NhDivisionService> localizer,
        INhDbLogService dbLogService,
        LogHelperService logHelperService,
        ValidationService validationService,
        IMapper mapper
        )
    {
        _repository = repository;
        _localizer = localizer;
        _mapper = mapper;
        _dbLogService = dbLogService;
        _logHelper = logHelperService;
        _validationService = validationService;
    }

    protected TaskResult Validate()
    { 
        
    }

    public async Task<TaskResult<NhNotification>> CreateAsync(NhNotification notification, CancellationToken cancellationToken = default)
    {
        var taskResult = new TaskResult<NhNotification>();

        if (notification == null)
        {
            taskResult.AddError("Notification cannot be null.");
            return taskResult;
        }

        using var transaction = await _repository.StartOrGetTransactionScopeAsync(cancellationToken);

        try
        {

            await _repository.AddAsync(notification);
            await _repository.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch(Exception ex)
        {
            taskResult.AddError(string.Empty, _localizer["An error occurred while creating the notification."]);
            return taskResult;
        }

        return taskResult;
    }
}