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

namespace NewHeap.Platform.AspNet.Common.Services;

public partial class NhDivisionUserService : NhDivisionUserService<NhUser, NhDivision, NhDivisionUser, NhDivisionRole, NhDivisionUserRole, NhDivisionRoleClaim, NhDivisionUserMutateModel>
{
    public NhDivisionUserService(
        IRepository<NhDivisionUser> divisionUserRepository,
        IRepository<NhDivisionUserRole> divisionUserRoleRepository,
        IStringLocalizer<NhDivisionUserService> localizer,
        INhDbLogService dbLogService,
        LogHelperService logHelperService,
        ValidationService validationService,
        IMapper mapper)
        : base(divisionUserRepository, divisionUserRoleRepository, localizer, dbLogService, logHelperService, validationService, mapper)
    {
    }
}

public abstract partial class NhDivisionUserService<
    TUser,
    TDivision,
    TDivisionUser,
    TDivisionRole,
    TDivisionUserRole,
    TDivisionRoleClaim,
    TDivisionUserMutateModel
    > : BaseDbEntityService<TDivisionUser, TDivisionUserMutateModel, NhDivisionUserService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TDivisionUserMutateModel>>
        where TUser : NhUser<TDivision, TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TUser>
    where TDivision : NhDivision<TDivisionUser, TDivisionUserRole, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionRole : NhDivisionRole<TDivisionUserRole, TDivisionRoleClaim, TDivisionUser, TDivisionRole, TDivision, TUser>, new()
    where TDivisionUser : NhDivisionUser<TDivisionUserRole, TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivision, TUser>
    where TDivisionUserRole : NhDivisionUserRole<TDivisionUser, TDivisionRole, TDivisionRoleClaim, TDivisionUserRole, TDivision, TUser>, new()
    where TDivisionRoleClaim : NhDivisionRoleClaim
    where TDivisionUserMutateModel : NhDivisionUserMutateModel
{
    protected readonly IRepository<TDivisionUserRole> _divisionUserRoleRepository;
    protected readonly IStringLocalizer _localizer;
    protected readonly IMapper _mapper;

    public NhDivisionUserService(
        IRepository<TDivisionUser> divisionUserRepository,
        IRepository<TDivisionUserRole> divisionUserRoleRepository,
        IStringLocalizer<NhDivisionUserService<TUser, TDivision, TDivisionUser, TDivisionRole, TDivisionUserRole, TDivisionRoleClaim, TDivisionUserMutateModel>> localizer,
        INhDbLogService dbLogService,
        LogHelperService logHelperService,
        ValidationService validationService,
        IMapper mapper)
        : base(divisionUserRepository, dbLogService, logHelperService, mapper, localizer, validationService)
    {
        _divisionUserRoleRepository = divisionUserRoleRepository;
        _mapper = mapper;
        _localizer = localizer;
    }

    protected override async Task ValidateCreateUpdateDeleteAsync(
        CreateUpdateDeleteValidateModel<TDivisionUser, TDivisionUser, TDivisionUserMutateModel> model, CancellationToken cancellationToken = default)
    {
        void sourceModelCheck()
        {
            if (model.SourceModel == null)
            {
                model.TaskResult.AddError(string.Empty, _localizer["Action type requires a source model."]);
            }
        }

        if (model.ActionType == CRUDActionType.Create)
        {
            _validationService.ValidateMutateModelModelState(model);

            if (await _repository.AnyAsync(x =>
                    x.DivisionId == model.MutateModel!.DivisionId!.Value && x.UserId == model.MutateModel.UserId))
            {
                model.TaskResult.AddError(string.Empty, _localizer["Mapping already exists."]);
            }
        }
        else if (model.ActionType == CRUDActionType.Update)
        {
            _validationService.ValidateMutateModelModelState(model);

            sourceModelCheck();

            if (await _repository.AnyAsync(x =>
                    x.Id != model.SourceModel!.Id && 
                    x.DivisionId == model.MutateModel!.DivisionId!.Value &&
                    x.UserId == model.MutateModel.UserId))
            {
                model.TaskResult.AddError(string.Empty, _localizer["Mapping already exists."]);
            }
        }
        else if (model.ActionType == CRUDActionType.Delete)
        {
            sourceModelCheck();
        }
    }

    public override IQueryable<TDivisionUser> QueryableWithAllIncludes(IQueryable<TDivisionUser>? queryable = null)
    {
        queryable = base.QueryableWithAllIncludes(queryable);
        queryable = queryable
            .Include(x => x.User)
            .Include(x => x.Division)
            .Include(x => x.DivisionUserRoles)
            .ThenInclude(x => x.DivisionRole);

        return queryable;
    }

    public override async Task<TaskResult<TDivisionUser?>> CreateAsync(TDivisionUserMutateModel mutateModel,
        Guid? committedByUserId = default, Action<TDivisionUser>? beforeSave = null, CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null)
    {
        TaskResult<TDivisionUser> result = new();

        await ValidateCreateUpdateDeleteAsync(
            new CreateUpdateDeleteValidateModel<TDivisionUser, TDivisionUser, TDivisionUserMutateModel>(CRUDActionType
                .Create) { TaskResult = result, SourceModel = null, MutateModel = mutateModel });

        if (!result.Success)
        {
            return result;
        }

        var divisionUser = _mapper.Map<TDivisionUser>(mutateModel);
        await _repository.AddAsync(divisionUser);

        if (mutateModel.RoleIds?.Any() == true)
        {
            IEnumerable<TDivisionUserRole> divisionUserRoles = mutateModel.RoleIds.Select(roleId => new TDivisionUserRole
            {
                DivisionUser = divisionUser, DivisionRoleId = roleId
            });

            await _divisionUserRoleRepository.AddRangeAsync(divisionUserRoles);
        }

        beforeSave?.Invoke(divisionUser);

        await _dbLogService.LogAsync(
            "DivisionUser create successful.",
            messageArguments: new[] { divisionUser.Id.ToString() },
            objectId: divisionUser.Id.ToString(),
            objectType: typeof(TDivisionUser).Name,
            objectTypeFull: typeof(TDivisionUser).FullName,
            userId: committedByUserId,
            action: LogAction.Create,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _repository.Context
        );

        if (options?.SaveChangesDisabled != true)
        { 
            await _repository.SaveChangesAsync();
        }

        divisionUser = await GetAsync(divisionUser.Id);
        result.Data = divisionUser;

        return result;
    }

    public override async Task<TaskResult<TDivisionUser?>> UpdateAsync(
        Guid id, 
        TDivisionUserMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<TDivisionUser>? beforeSave = null, 
        CancellationToken cancellationToken = default,
        BaseDbEntityServiceOperationOptions? options = null
        )
    {
        TaskResult<TDivisionUser> result = new();

        var divisionUser = await _repository.FindOneByAsync(x => x.Id == id);

        await ValidateCreateUpdateDeleteAsync(
            new CreateUpdateDeleteValidateModel<TDivisionUser, TDivisionUser, TDivisionUserMutateModel>(CRUDActionType
                .Update) { TaskResult = result, SourceModel = divisionUser, MutateModel = mutateModel });

        if (!result.Success)
        {
            return result;
        }

        var originalData = LogHelperService.Copy(divisionUser);

        divisionUser = _mapper.Map(mutateModel, divisionUser)!;
        divisionUser.LastModifiedDateTime = DateTime.UtcNow;
        beforeSave?.Invoke(divisionUser);

        foreach (var divisionRoleId in mutateModel.RoleIds)
        {
            if (await _divisionUserRoleRepository.AnyAsync(x =>
                    x.DivisionUser.UserId == mutateModel.UserId && x.DivisionRoleId == divisionRoleId &&
                    x.DivisionUser.DivisionId == mutateModel.DivisionId))
            {
                continue;
            }

            TDivisionUserRole divisionUserRole = new() { DivisionRoleId = divisionRoleId, DivisionUser = divisionUser };

            await _divisionUserRoleRepository.AddAsync(divisionUserRole);
        }

        var updatedData = LogHelperService.Copy(divisionUser);

        var changedProperties = await _logHelper.ChangedProperties(originalData,
            updatedData, new Dictionary<Expression<Func<TDivisionUser?, object>>, Func<object?, Task<string>>>
            {
                // Method resolvers
            },
            x => x!.UserId,
            x => x!.DivisionId
        );

        if (changedProperties.Any())
        {
            var values = string.Join("\n",
                changedProperties.Select(x => $"{x.Key}: '{x.OriginalValue}' -> '{x.UpdateValue}'"));
            await _dbLogService.LogAsync(
                "Entity values updated",
                messageArguments: new[] { values },
                objectId: divisionUser.Id.ToString(),
                objectType: typeof(TDivisionUser).Name,
                objectTypeFull: typeof(TDivisionUser).FullName,
                userId: committedByUserId,
                action: LogAction.Update,
                type: LogType.Information,
                source: LogSource.Internal,
                tag: GetType().Name
            );
        }

        await _dbLogService.LogAsync(
            "DivisionUser update successful.",
            messageArguments: new[] { divisionUser.Id.ToString() },
            objectId: divisionUser.Id.ToString(),
            objectType: typeof(TDivisionUser).Name,
            objectTypeFull: typeof(TDivisionUser).FullName,
            userId: committedByUserId,
            action: LogAction.Update,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _repository.Context
        );

        if (options?.SaveChangesDisabled != true)
        {
            await _repository.SaveChangesAsync();
        }

        await _divisionUserRoleRepository
            .GetAll()
            .Where(x => x.DivisionUserId == divisionUser.Id && !mutateModel.RoleIds.Contains(x.DivisionRoleId))
            .ExecuteDeleteAsync();

        divisionUser = await GetAsync(divisionUser.Id);
        result.Data = divisionUser;

        return result;
    }
}