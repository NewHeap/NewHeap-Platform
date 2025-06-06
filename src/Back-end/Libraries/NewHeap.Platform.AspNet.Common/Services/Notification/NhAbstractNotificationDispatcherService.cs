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

public abstract partial class NhAbstractNotificationDispatcherService
{
    protected readonly IRepository<NhNotification> _repository;
    protected readonly IStringLocalizer<NhDivisionService> _localizer;
    protected readonly INhDbLogService _dbLogService;
    protected readonly IMapper _mapper;
    protected readonly LogHelperService _logHelper;
    protected readonly ValidationService _validationService;

    public NhAbstractNotificationDispatcherService(
        IRepository<NhNotification> repository,
        IStringLocalizer<NhDivisionService> localizer,
        INhDbLogService dbLogService,
        LogHelperService logHelperService,
        ValidationService validationService,
        IMapper mapper)
    {
        _repository = repository;
        _localizer = localizer;
        _mapper = mapper;
        _dbLogService = dbLogService;
        _logHelper = logHelperService;
        _validationService = validationService;
    }
}