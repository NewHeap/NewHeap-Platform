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

public partial class DivisionService : BaseDbEntityService<NhDivision, DivisionMutateModel, DivisionService>
{
    protected readonly IRepository<NhDivision> _divisionRepository;
    protected readonly IRepository<NhDivisionRoleClaim> _divisionRoleClaimRepository;
    protected readonly IRepository<NhDivisionRole> _divisionRoleRepository;
    protected readonly IRepository<NhDivisionUser> _divisionUserRepository;
    protected readonly IRepository<NhDivisionUserRole> _divisionUserRoleRepository;

    public DivisionService(
        IRepository<NhDivision> divisionRepository,
        IRepository<NhDivisionRole> divisionRoleRepository,
        IRepository<NhDivisionUser> divisionUserRepository,
        IRepository<NhDivisionUserRole> divisionUserRoleRepository,
        IRepository<NhDivisionRoleClaim> divisionRoleClaimRepository,
        IStringLocalizer<DivisionService> localizer,
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

    public IRepository<NhDivisionRole> GetRoleRepository()
    {
        return _divisionRoleRepository;
    }

    public IRepository<NhDivisionRoleClaim> GetRoleClaimRepository()
    {
        return _divisionRoleClaimRepository;
    }

    public override async Task ValidateCreateUpdateDeleteAsync(
        CreateUpdateDeleteValidateModel<NhDivision, NhDivision, DivisionMutateModel> model, CancellationToken cancellationToken)
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

    protected override Task<IEnumerable<ChangedValue>> OnUpdateGetChangedProperies(NhDivision original, NhDivision updated, CancellationToken cancellationToken = default)
    {
        return _logHelper.ChangedProperties(original, updated, new Dictionary<Expression<Func<NhDivision, object>>, Func<object, Task<string>>>
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

    public async Task<TaskResult<NhDivisionRole>> RoleCreateAsync(string roleName, Guid? committedByUserId = default)
    {
        TaskResult<NhDivisionRole> result = new();

        NhDivisionRole divisionRole = new() { Name = roleName };
        await _divisionRoleRepository.AddAsync(divisionRole);

        await _dbLogService.LogAsync(
            "Division role create successful.",
            messageArguments: new[] { divisionRole.Id.ToString() },
            objectId: divisionRole.Id.ToString(),
            objectType: typeof(NhDivisionRole).Name,
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

    public async Task<TaskResult<NhDivisionRole>> RoleDeleteAsync(string roleName, Guid? committedByUserId = default)
    {
        TaskResult<NhDivisionRole> result = new();

        var divisionRole = await _divisionRoleRepository.FindOneByAsync(x => x.Name == roleName);

        _divisionRoleRepository.Remove(divisionRole!);

        await _dbLogService.LogAsync(
            "Division role delete successful.",
            messageArguments: new[] { divisionRole!.Id.ToString() },
            objectId: divisionRole.Id.ToString(),
            objectType: typeof(NhDivisionRole).Name,
            objectTypeFull: typeof(NhDivisionRole).FullName,
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