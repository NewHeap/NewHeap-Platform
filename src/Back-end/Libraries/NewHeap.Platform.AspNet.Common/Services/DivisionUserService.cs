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

public partial class DivisionUserService : BaseDbEntityService<NhDivisionUser, DivisionUserMutateModel, DivisionUserService>
{
    protected readonly IRepository<NhDivisionUserRole> _divisionUserRoleRepository;
    protected readonly IStringLocalizer _localizer;
    protected readonly IMapper _mapper;

    public DivisionUserService(
        IRepository<NhDivisionUser> divisionUserRepository,
        IRepository<NhDivisionUserRole> divisionUserRoleRepository,
        IStringLocalizer<DivisionUserService> localizer,
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

    public override async Task ValidateCreateUpdateDeleteAsync(
        CreateUpdateDeleteValidateModel<NhDivisionUser, NhDivisionUser, DivisionUserMutateModel> model, CancellationToken cancellationToken = default)
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

    public override async Task<TaskResult<NhDivisionUser>> CreateAsync(DivisionUserMutateModel mutateModel,
        Guid? committedByUserId = default, Action<NhDivisionUser>? beforeSave = null, CancellationToken cancellationToken = default)
    {
        TaskResult<NhDivisionUser> result = new();

        await ValidateCreateUpdateDeleteAsync(
            new CreateUpdateDeleteValidateModel<NhDivisionUser, NhDivisionUser, DivisionUserMutateModel>(CRUDActionType
                .Create) { TaskResult = result, SourceModel = null, MutateModel = mutateModel });

        if (!result.Success)
        {
            return result;
        }

        var divisionUser = _mapper.Map<NhDivisionUser>(mutateModel);
        await _repository.AddAsync(divisionUser);

        if (mutateModel.RoleIds?.Any() == true)
        {
            IEnumerable<NhDivisionUserRole> divisionUserRoles = mutateModel.RoleIds.Select(roleId => new NhDivisionUserRole
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
            objectType: typeof(NhDivisionUser).Name,
            objectTypeFull: typeof(NhDivisionUser).FullName,
            userId: committedByUserId,
            action: LogAction.Create,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _repository.Context
        );

        await _repository.SaveChangesAsync();

        result.Data = divisionUser;

        return result;
    }

    public override async Task<TaskResult<NhDivisionUser>> UpdateAsync(
        Guid id, 
        DivisionUserMutateModel mutateModel,
        Guid? committedByUserId = default,
        Action<NhDivisionUser>? beforeSave = null, 
        CancellationToken cancellationToken = default
        )
    {
        TaskResult<NhDivisionUser> result = new();

        var divisionUser = await _repository.FindOneByAsync(x => x.Id == id);

        await ValidateCreateUpdateDeleteAsync(
            new CreateUpdateDeleteValidateModel<NhDivisionUser, NhDivisionUser, DivisionUserMutateModel>(CRUDActionType
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

            NhDivisionUserRole divisionUserRole = new() { DivisionRoleId = divisionRoleId, DivisionUser = divisionUser };

            await _divisionUserRoleRepository.AddAsync(divisionUserRole);
        }

        var updatedData = LogHelperService.Copy(divisionUser);

        var changedProperties = await _logHelper.ChangedProperties(originalData,
            updatedData, new Dictionary<Expression<Func<NhDivisionUser?, object>>, Func<object?, Task<string>>>
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
                objectType: typeof(NhDivisionUser).Name,
                objectTypeFull: typeof(NhDivisionUser).FullName,
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
            objectType: typeof(NhDivisionUser).Name,
            objectTypeFull: typeof(NhDivisionUser).FullName,
            userId: committedByUserId,
            action: LogAction.Update,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _repository.Context
        );

        await _repository.SaveChangesAsync();
        await _divisionUserRoleRepository
            .GetAll()
            .Where(x => x.DivisionUserId == divisionUser.Id && !mutateModel.RoleIds.Contains(x.DivisionRoleId))
            .ExecuteDeleteAsync();

        result.Data = divisionUser;

        return result;
    }
}