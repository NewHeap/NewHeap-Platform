using NewHeap.Platform.Mapping;
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

public partial class NhDivisionService : NhDivisionService<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole, NhDivisionRoleClaim, NhDivisionMutateModel>
{
    public NhDivisionService(
        IRepository<NhDivision> divisionRepository,
        IRepository<NhDivisionRole> divisionRoleRepository,
        IRepository<NhDivisionUser> divisionUserRepository,
        IRepository<NhDivisionUserRole> divisionUserRoleRepository,
        IRepository<NhDivisionRoleClaim> divisionRoleClaimRepository,
        IStringLocalizer<NhDivisionService> localizer,
        INhDbLogService dbLogService,
        LogHelperService logHelperService,
        ValidationService validationService,
        IMapper mapper)
        : base(divisionRepository, divisionRoleRepository, divisionUserRepository, divisionUserRoleRepository, divisionRoleClaimRepository, localizer, dbLogService, logHelperService, validationService, mapper)
    {
    }
}

public abstract partial class NhDivisionService<
    TUser,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim,
    TDivisionMutateModel
    > : BaseDbEntityService<TDivision, TDivisionMutateModel, NhDivisionService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TDivisionMutateModel>>
    where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>, new()
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TDivisionMutateModel : NhDivisionMutateModel
{
    protected readonly IRepository<TDivision> _divisionRepository;
    protected readonly IRepository<TDivisionRoleClaim> _divisionRoleClaimRepository;
    protected readonly IRepository<TDivisionRole> _divisionRoleRepository;
    protected readonly IRepository<TDivisionUser> _divisionUserRepository;
    protected readonly IRepository<TDivisionUserRole> _divisionUserRoleRepository;

    public NhDivisionService(
        IRepository<TDivision> divisionRepository,
        IRepository<TDivisionRole> divisionRoleRepository,
        IRepository<TDivisionUser> divisionUserRepository,
        IRepository<TDivisionUserRole> divisionUserRoleRepository,
        IRepository<TDivisionRoleClaim> divisionRoleClaimRepository,
        IStringLocalizer<NhDivisionService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TDivisionMutateModel>> localizer,
        INhDbLogService dbLogService,
        LogHelperService logHelperService,
        ValidationService validationService,
        IMapper mapper)
            : base(divisionRepository, dbLogService, logHelperService, mapper, localizer, validationService)
    {
        _divisionRepository = divisionRepository;
        _divisionRoleRepository = divisionRoleRepository;
        _divisionUserRepository = divisionUserRepository;
        _divisionUserRoleRepository = divisionUserRoleRepository;
        _divisionRoleClaimRepository = divisionRoleClaimRepository;
    }

    public IRepository<TDivisionRole> GetRoleRepository()
    {
        return _divisionRoleRepository;
    }

    public IRepository<TDivisionRoleClaim> GetRoleClaimRepository()
    {
        return _divisionRoleClaimRepository;
    }

    protected override async Task ValidateCreateUpdateDeleteAsync(
        CreateUpdateDeleteValidateModel<TDivision, TDivision, TDivisionMutateModel> model, CancellationToken cancellationToken)
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

    protected override Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperties(TDivision? original, TDivision? updated, CancellationToken cancellationToken = default)
    {
        return _logHelper.ChangedProperties(original, updated, new Dictionary<Expression<Func<TDivision?, object?>>, Func<object?, Task<string?>>>
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

    public async Task<TaskResult<TDivisionRole>> RoleCreateAsync(string roleName, Guid? committedByUserId = default)
    {
        TaskResult<TDivisionRole> result = new();

        TDivisionRole divisionRole = new() { Name = roleName };
        await _divisionRoleRepository.AddAsync(divisionRole);

        await _dbLogService.LogAsync(
            "Division role create successful.",
            messageArguments: new[] { divisionRole.Id.ToString() },
            objectId: divisionRole.Id.ToString(),
            objectType: typeof(TDivisionRole).Name,
            objectTypeFull: typeof(NhDivisionRole).FullName,
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

    public async Task<TaskResult<TDivisionRole>> RoleDeleteAsync(string roleName, Guid? committedByUserId = default)
    {
        TaskResult<TDivisionRole> result = new();

        var divisionRole = await _divisionRoleRepository.FindOneByAsync(x => x.Name == roleName);

        _divisionRoleRepository.Remove(divisionRole!);

        await _dbLogService.LogAsync(
            "Division role delete successful.",
            messageArguments: new[] { divisionRole!.Id.ToString() },
            objectId: divisionRole.Id.ToString(),
            objectType: typeof(TDivisionRole).Name,
            objectTypeFull: typeof(TDivisionRole).FullName,
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