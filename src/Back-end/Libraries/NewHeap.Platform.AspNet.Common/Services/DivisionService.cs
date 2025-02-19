using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models;
using NewHeap.Platform.Common.Services;
using System.Linq.Expressions;
using System.Security.Claims;

namespace NewHeap.Platform.AspNet.Common.Services;

public partial class DivisionService
{
    protected readonly DbLogService _dbLogService;
    protected readonly IRepository<Division> _divisionRepository;
    protected readonly IRepository<DivisionRoleClaim> _divisionRoleClaimRepository;
    protected readonly IRepository<DivisionRole> _divisionRoleRepository;
    protected readonly IRepository<DivisionUser> _divisionUserRepository;
    protected readonly IRepository<DivisionUserRole> _divisionUserRoleRepository;
    protected readonly IStringLocalizer _localizer;
    protected readonly LogHelperService _logHelperService;
    protected readonly IMapper _mapper;
    protected readonly ValidationService _validationService;

    public DivisionService(
        IRepository<Division> divisionRepository,
        IRepository<DivisionRole> divisionRoleRepository,
        IRepository<DivisionUser> divisionUserRepository,
        IRepository<DivisionUserRole> divisionUserRoleRepository,
        IRepository<DivisionRoleClaim> divisionRoleClaimRepository,
        IStringLocalizer<DivisionService> localizer,
        DbLogService dbLogManager,
        LogHelperService logHelper,
        ValidationService validationManager,
        IMapper mapper)
    {
        _divisionRepository = divisionRepository;
        _divisionRoleRepository = divisionRoleRepository;
        _divisionUserRepository = divisionUserRepository;
        _divisionUserRoleRepository = divisionUserRoleRepository;
        _divisionRoleClaimRepository = divisionRoleClaimRepository;
        _validationService = validationManager;
        _mapper = mapper;
        _localizer = localizer;
        _dbLogService = dbLogManager;
        _logHelperService = logHelper;
    }

    public IRepository<Division> GetRepository()
    {
        return _divisionRepository;
    }

    public IRepository<DivisionRole> GetRoleRepository()
    {
        return _divisionRoleRepository;
    }

    public IRepository<DivisionRoleClaim> GetRoleClaimRepository()
    {
        return _divisionRoleClaimRepository;
    }

    public async Task ValidateCreateUpdateDeleteAsync(
        CreateUpdateDeleteValidateModel<Division, Division, DivisionMutateModel> model)
    {
        void sourceModelCheck()
        {
            if (model.SourceModel == null)
            {
                model.TaskResult.AddError(string.Empty, _localizer["Action type requires a source model."]);
            }
        }

        void validateTimeZone()
        {
            if (!TimeZoneInfo.GetSystemTimeZones().Any(x => x.Id.Equals(model.MutateModel.TimeZoneId)))
            {
                model.TaskResult.AddError(nameof(model.MutateModel.TimeZoneId),
                    _localizer["Invalid time zone id provided."]);
            }
        }

        if (model.ActionType == CRUDActionType.Create)
        {
            _validationService.ValidateMutateModelModelState(model);

            if (await _divisionRepository.AnyAsync(x =>
                    x.Name.Trim().ToLower() == model.MutateModel.Name.Trim().ToLower()))
            {
                model.TaskResult.AddError(nameof(model.MutateModel.Name), _localizer["Name is already exists."]);
            }

            validateTimeZone();
        }
        else if (model.ActionType == CRUDActionType.Update)
        {
            _validationService.ValidateMutateModelModelState(model);

            sourceModelCheck();

            if (await _divisionRepository.AnyAsync(x =>
                    x.Id != model.SourceModel.Id && x.Name.Trim().ToLower() == model.MutateModel.Name.Trim().ToLower()))
            {
                model.TaskResult.AddError(nameof(model.MutateModel.Name), _localizer["Name is already exists."]);
            }

            validateTimeZone();
        }
        else if (model.ActionType == CRUDActionType.Delete)
        {
            sourceModelCheck();
        }
    }

    public async Task<TaskResult<Division>> CreateAsync(DivisionMutateModel mutateModel,
        Guid? committedByUserId = default)
    {
        TaskResult<Division> result = new();

        await ValidateCreateUpdateDeleteAsync(
            new CreateUpdateDeleteValidateModel<Division, Division, DivisionMutateModel>(CRUDActionType.Create)
            {
                TaskResult = result, SourceModel = null, MutateModel = mutateModel
            });

        if (!result.Success)
        {
            return result;
        }

        var division = _mapper.Map<Division>(mutateModel);
        await _divisionRepository.AddAsync(division);

        await _dbLogService.LogAsync(
            "Division create successful.",
            messageArguments: new[] { division.Id.ToString() },
            objectId: division.Id.ToString(),
            objectType: typeof(Division).Name,
            objectTypeFull: typeof(Division).FullName,
            userId: committedByUserId,
            action: LogAction.Create,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _divisionRepository.Context
        );

        await _divisionRepository.SaveChangesAsync();

        result.Data = division;

        return result;
    }

    public async Task<TaskResult<Division>> UpdateAsync(Guid id, DivisionMutateModel mutateModel,
        Guid? committedByUserId = default)
    {
        TaskResult<Division> result = new();

        var division = await _divisionRepository.FindOneByAsync(x => x.Id == id);

        await ValidateCreateUpdateDeleteAsync(
            new CreateUpdateDeleteValidateModel<Division, Division, DivisionMutateModel>(CRUDActionType.Update)
            {
                TaskResult = result, SourceModel = division, MutateModel = mutateModel
            });

        if (!result.Success)
        {
            return result;
        }

        var currentDivisionName = division.Name;

        var originalData = LogHelperService.Copy(division);

        division = _mapper.Map(mutateModel, division);
        division.LastModifiedDateTime = DateTimeOffset.UtcNow;

        var updatedData = LogHelperService.Copy(division);

        var changedProperties = await _logHelperService.ChangedProperties(originalData,
            updatedData, new Dictionary<Expression<Func<Division, object>>, Func<object, Task<string>>>
            {
                // Method resolvers
            },
            x => x.Name,
            x => x.Description,
            x => x.UserSelectAllowed,
            x => x.TimeZoneId
        );

        if (changedProperties.Any())
        {
            var values = string.Join("\n",
                changedProperties.Select(x => $"{x.Key}: '{x.OriginalValue}' -> '{x.UpdateValue}'"));
            await _dbLogService.LogAsync(
                "Entity values updated",
                messageArguments: new[] { values },
                objectId: division.Id.ToString(),
                objectType: typeof(Division).Name,
                objectTypeFull: typeof(Division).FullName,
                userId: committedByUserId,
                action: LogAction.Update,
                type: LogType.Information,
                source: LogSource.Internal,
                tag: GetType().Name
            );
        }

        await _dbLogService.LogAsync(
            "Division update successful.",
            messageArguments: new[] { division.Id.ToString() },
            objectId: division.Id.ToString(),
            objectType: typeof(Division).Name,
            objectTypeFull: typeof(Division).FullName,
            userId: committedByUserId,
            action: LogAction.Update,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _divisionRepository.Context
        );

        await _divisionRepository.SaveChangesAsync();

        result.Data = division;

        return result;
    }

    public async Task<TaskResult<Division>> DeleteAsync(Guid id, Guid? committedByUserId = default)
    {
        TaskResult<Division> result = new();

        var division = await _divisionRepository
            .FindOneByAsync(x => x.Id == id);

        await ValidateCreateUpdateDeleteAsync(
            new CreateUpdateDeleteValidateModel<Division, Division, DivisionMutateModel>(CRUDActionType.Delete)
            {
                TaskResult = result, SourceModel = division, MutateModel = null
            });

        if (!result.Success)
        {
            return result;
        }

        result.Data = division;
        _divisionRepository.Remove(division);

        await _dbLogService.LogAsync(
            "Division remove successful.",
            messageArguments: new[] { division.Id.ToString() },
            objectId: division.Id.ToString(),
            objectType: typeof(Division).Name,
            objectTypeFull: typeof(Division).FullName,
            userId: committedByUserId,
            action: LogAction.Delete,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _divisionRepository.Context
        );

        await _divisionRepository.SaveChangesAsync();

        return result;
    }

    #region Roles

    public Task<bool> RoleExistsAsync(string roleName)
    {
        return _divisionRoleRepository.AnyAsync(x => x.Name.ToLower().Trim() == roleName.ToLower().Trim());
    }

    public async Task<TaskResult<DivisionRole>> RoleCreateAsync(string roleName, Guid? committedByUserId = default)
    {
        TaskResult<DivisionRole> result = new();

        DivisionRole divisionRole = new() { Name = roleName };
        await _divisionRoleRepository.AddAsync(divisionRole);

        await _dbLogService.LogAsync(
            "Division role create successful.",
            messageArguments: new[] { divisionRole.Id.ToString() },
            objectId: divisionRole.Id.ToString(),
            objectType: typeof(DivisionRole).Name,
            objectTypeFull: typeof(DivisionRole).FullName,
            userId: committedByUserId,
            action: LogAction.Create,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _divisionRoleRepository.Context
        );

        await _divisionRoleRepository.SaveChangesAsync();

        result.Data = divisionRole;

        return result;
    }

    public async Task<TaskResult<DivisionRole>> RoleDeleteAsync(string roleName, Guid? committedByUserId = default)
    {
        TaskResult<DivisionRole> result = new();

        var divisionRole = await _divisionRoleRepository.FindOneByAsync(x => x.Name == roleName);

        _divisionRoleRepository.Remove(divisionRole);

        await _dbLogService.LogAsync(
            "Division role delete successful.",
            messageArguments: new[] { divisionRole.Id.ToString() },
            objectId: divisionRole.Id.ToString(),
            objectType: typeof(DivisionRole).Name,
            objectTypeFull: typeof(DivisionRole).FullName,
            userId: committedByUserId,
            action: LogAction.Delete,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _divisionRoleRepository.Context
        );

        await _divisionRoleRepository.SaveChangesAsync();

        result.Data = divisionRole;

        return result;
    }

    #endregion

    #region RoleClaims

    public Task<bool> RoleClaimExistsAsync(Guid roleId, string claimType, string claimValue)
    {
        return _divisionRoleClaimRepository.AnyAsync(x => x.DivisionRoleId == roleId
                                                          && x.ClaimType.ToLower().Trim() == claimType.ToLower().Trim()
                                                          && x.ClaimValue.ToLower().Trim() ==
                                                          claimValue.ToLower().Trim()
        );
    }

    public async Task<IEnumerable<Claim>> RoleClaimsAsync(Guid roleId)
    {
        return (await _divisionRoleClaimRepository.GetAll().Where(x => x.DivisionRoleId == roleId).ToListAsync())
            .Select(x =>
            {
                Claim claim = new(x.ClaimType, x.ClaimValue);
                return claim;
            });
    }

    #endregion
}