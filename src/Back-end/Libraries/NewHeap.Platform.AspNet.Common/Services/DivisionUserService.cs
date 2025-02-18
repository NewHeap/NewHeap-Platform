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

namespace NewHeap.Platform.AspNet.Common.Services;

public partial class DivisionUserService
{
    protected readonly IRepository<DivisionUser> _divisionUserRepository;
    protected readonly IRepository<DivisionUserRole> _divisionUserRoleRepository;
    protected readonly DbLogService _logService;
    protected readonly LogHelperService _logHelperService;
    protected readonly IStringLocalizer _localizer;
    protected readonly IMapper _mapper;
    protected readonly ValidationService _validationService;
    protected readonly NhUserManager _userManager;

    public DivisionUserService(
        IRepository<DivisionUser> divisionUserRepository,
        IRepository<DivisionUserRole> divisionUserRoleRepository,
        IStringLocalizer<DivisionUserService> localizer,
        DbLogService logManager,
        LogHelperService logHelper,
        NhUserManager userManager,
        ValidationService validationManager,
        IMapper mapper)
    {
        _divisionUserRepository = divisionUserRepository;
        _divisionUserRoleRepository = divisionUserRoleRepository;
        _mapper = mapper;
        _localizer = localizer;
        _logService = logManager;
        _logHelperService = logHelper;
        _validationService = validationManager;
        _userManager = userManager;
    }

    public IRepository<DivisionUser> GetRepository()
    {
        return _divisionUserRepository;
    }

    public async Task ValidateCreateUpdateDeleteAsync(CreateUpdateDeleteValidateModel<DivisionUser, DivisionUser, DivisionUserMutateModel> model)
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

            if (await _divisionUserRepository.AnyAsync(x => x.DivisionId == model.MutateModel.DivisionId.Value && x.UserId == model.MutateModel.UserId))
            {
                model.TaskResult.AddError(string.Empty, _localizer["Mapping already exists."]);
            }
        }
        else if (model.ActionType == CRUDActionType.Update)
        {
            _validationService.ValidateMutateModelModelState(model);

            sourceModelCheck();

            if (await _divisionUserRepository.AnyAsync(x => x.Id != model.SourceModel.Id && x.DivisionId == model.MutateModel.DivisionId.Value && x.UserId == model.MutateModel.UserId))
            {
                model.TaskResult.AddError(string.Empty, _localizer["Mapping already exists."]);
            }
        }
        else if (model.ActionType == CRUDActionType.Delete)
        {
            sourceModelCheck();
        }
        else
        {

        }
    }

    public async Task<TaskResult<DivisionUser>> CreateAsync(DivisionUserMutateModel mutateModel, Guid? committedByUserId = default)
    {
        var result = new TaskResult<DivisionUser>();

        await ValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<DivisionUser, DivisionUser, DivisionUserMutateModel>(CRUDActionType.Create) { 
            TaskResult = result,
            SourceModel = null,
            MutateModel = mutateModel,
        });

        if (!result.Success)
        {
            return result;
        }

        var divisionUser = _mapper.Map<DivisionUser>(mutateModel);
        await _divisionUserRepository.AddAsync(divisionUser);

        if (mutateModel.RoleIds?.Any() == true)
        {
            var divisionUserRoles = mutateModel.RoleIds.Select(roleId => new DivisionUserRole()
            {
                DivisionUser = divisionUser,
                DivisionRoleId = roleId
            });

            await _divisionUserRoleRepository.AddRangeAsync(divisionUserRoles);
        }

        await _logService.LogAsync(
            message: "DivisionUser create successful.",
            messageArguments: new string[] {
                divisionUser.Id.ToString()
            },
            objectId: divisionUser.Id.ToString(),
            objectType: (typeof(DivisionUser)).Name,
            objectTypeFull: (typeof(DivisionUser)).FullName,
            userId: committedByUserId,
            action: LogAction.Create,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _divisionUserRepository.Context
        );

        await _divisionUserRepository.SaveChangesAsync();

        result.Data = divisionUser;

        return result;
    }

    public async Task<TaskResult<DivisionUser>> UpdateAsync(Guid id, DivisionUserMutateModel mutateModel, Guid? committedByUserId = default)
    {
        var result = new TaskResult<DivisionUser>();

        var divisionUser = await _divisionUserRepository.FindOneByAsync(x => x.Id == id);

        await ValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<DivisionUser, DivisionUser, DivisionUserMutateModel>(CRUDActionType.Update)
        {
            TaskResult = result,
            SourceModel = divisionUser,
            MutateModel = mutateModel,
        });

        if (!result.Success)
        {
            return result;
        }

        var originalData = LogHelperService.Copy(divisionUser);

        divisionUser = _mapper.Map(mutateModel, divisionUser);

        foreach (var divisionRoleId in mutateModel.RoleIds)
        {
            if (await _divisionUserRoleRepository.AnyAsync(x => x.DivisionUser.UserId == mutateModel.UserId && x.DivisionRoleId == divisionRoleId && x.DivisionUser.DivisionId == mutateModel.DivisionId))
            {
                continue;
            }

            var divisionUserRole = new DivisionUserRole()
            {
                DivisionRoleId = divisionRoleId,
                DivisionUser = divisionUser
            };

            await _divisionUserRoleRepository.AddAsync(divisionUserRole);
        }

        var updatedData = LogHelperService.Copy(divisionUser);

        var changedProperties = await _logHelperService.ChangedProperties(originalData, updatedData, new Dictionary<Expression<Func<DivisionUser, object>>, Func<object, Task<string>>>
        { 
            // Method resolvers
        },
            x => x.UserId,
            x => x.DivisionId
        );

        if (changedProperties.Any())
        {
            var values = string.Join("\n", changedProperties.Select(x => $"{x.Key}: '{x.OriginalValue}' -> '{x.UpdateValue}'"));
            await _logService.LogAsync(
                "Entity values updated",
                messageArguments: new string[]
                {
                    values
                },
                objectId: divisionUser.Id.ToString(),
                objectType: typeof(DivisionUser).Name,
                objectTypeFull: typeof(DivisionUser).FullName,
                userId: committedByUserId,
                action: LogAction.Update,
                type: LogType.Information,
                source: LogSource.Internal,
                tag: GetType().Name
            );
        } 

        await _logService.LogAsync(
            message: "DivisionUser update successful.",
            messageArguments: new string[] {
                divisionUser.Id.ToString()
            },
            objectId: divisionUser.Id.ToString(),
            objectType: (typeof(DivisionUser)).Name,
            objectTypeFull: (typeof(DivisionUser)).FullName,
            userId: committedByUserId,
            action: LogAction.Update,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _divisionUserRepository.Context
        );

        await _divisionUserRepository.SaveChangesAsync();
        await _divisionUserRoleRepository
            .GetAll()
            .Where(x => x.DivisionUserId == divisionUser.Id && !mutateModel.RoleIds.Contains(x.DivisionRoleId))
            .ExecuteDeleteAsync();

        result.Data = divisionUser;

        return result;
    }

    public async Task<TaskResult<DivisionUser>> DeleteAsync(Guid id, Guid? committedByUserId = default)
    {
        var result = new TaskResult<DivisionUser>();

        var divisionUser = await _divisionUserRepository
            .FindOneByAsync(x => x.Id == id);

        await ValidateCreateUpdateDeleteAsync(new CreateUpdateDeleteValidateModel<DivisionUser, DivisionUser, DivisionUserMutateModel>(CRUDActionType.Delete)
        {
            TaskResult = result,
            SourceModel = divisionUser,
            MutateModel = null,
        });

        if (!result.Success)
        {
            return result;
        }

        result.Data = divisionUser;
        _divisionUserRepository.Remove(divisionUser);

        await _logService.LogAsync(
            message: "DivisionUser remove successful.",
            messageArguments: new string[] {
                divisionUser.Id.ToString()
            },
            objectId: divisionUser.Id.ToString(),
            objectType: (typeof(DivisionUser)).Name,
            objectTypeFull: (typeof(DivisionUser)).FullName,
            userId: committedByUserId,
            action: LogAction.Delete,
            type: LogType.Information,
            source: LogSource.Internal,
            tag: GetType().Name,
            doSaveChanges: false,
            dbContext: _divisionUserRepository.Context
        );

        await _divisionUserRepository.SaveChangesAsync();

        return result;
    }
}
