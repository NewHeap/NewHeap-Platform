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

namespace NewHeap.Platform.AspNet.Common.Services;

public partial class DivisionService : BaseDbEntityService<Division, DivisionMutateModel, DivisionViewModel, DivisionService>
{
    protected readonly IRepository<Division> _divisionRepository;
    protected readonly IRepository<DivisionRoleClaim> _divisionRoleClaimRepository;
    protected readonly IRepository<DivisionRole> _divisionRoleRepository;
    protected readonly IRepository<DivisionUser> _divisionUserRepository;
    protected readonly IRepository<DivisionUserRole> _divisionUserRoleRepository;

    public DivisionService(
        IRepository<Division> divisionRepository,
        IRepository<DivisionRole> divisionRoleRepository,
        IRepository<DivisionUser> divisionUserRepository,
        IRepository<DivisionUserRole> divisionUserRoleRepository,
        IRepository<DivisionRoleClaim> divisionRoleClaimRepository,
        IStringLocalizer<DivisionService> localizer,
        DbLogService dbLogService,
        LogHelperService logHelperService,
        ValidationService validationService,
        NhUserManager userManager,
        IMapper mapper)
            : base(divisionRepository, dbLogService, logHelperService, mapper, localizer, validationService, userManager)
    {
        _divisionRepository = divisionRepository;
        _divisionRoleRepository = divisionRoleRepository;
        _divisionUserRepository = divisionUserRepository;
        _divisionUserRoleRepository = divisionUserRoleRepository;
        _divisionRoleClaimRepository = divisionRoleClaimRepository;
    }

    public IRepository<DivisionRole> GetRoleRepository()
    {
        return _divisionRoleRepository;
    }

    public IRepository<DivisionRoleClaim> GetRoleClaimRepository()
    {
        return _divisionRoleClaimRepository;
    }

    public override async Task ValidateCreateUpdateDeleteAsync(
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
            if (!TimeZoneInfo.GetSystemTimeZones().Any(x => x.Id.Equals(model.MutateModel!.TimeZoneId)))
            {
                model.TaskResult.AddError(nameof(model.MutateModel.TimeZoneId),
                    _localizer["Invalid time zone id provided."]);
            }
        }

        if (model.ActionType == CRUDActionType.Create)
        {
            _validationService.ValidateMutateModelModelState(model);

            if (await _divisionRepository.AnyAsync(x =>
                    x.Name.Trim().ToLower() == model.MutateModel!.Name!.Trim().ToLower()))
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
                    x.Id != model.SourceModel!.Id && x.Name.Trim().ToLower() == model.MutateModel!.Name!.Trim().ToLower()))
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

    protected override Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperies(Division original, Division updated)
    {
        return _logHelper.ChangedProperties(original, updated, new Dictionary<Expression<Func<Division, object>>, Func<object, Task<string>>>
        {
            // Method resolvers
        },
            x => x!.Name,
            x => x!.Description,
            x => x!.UserSelectAllowed,
            x => x!.TimeZoneId
        );
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

        _divisionRoleRepository.Remove(divisionRole!);

        await _dbLogService.LogAsync(
            "Division role delete successful.",
            messageArguments: new[] { divisionRole!.Id.ToString() },
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