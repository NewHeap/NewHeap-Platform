using NewHeap.Platform.Common.Models;
using System.ComponentModel.DataAnnotations;

namespace NewHeap.Platform.Common.Services;

public partial class ValidationService
{
    protected readonly IServiceProvider _serviceProvider;

    public ValidationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public virtual void ValidateMutateModelModelState<TTaskResult, TSourceObj, TMutateObj>(
        CreateUpdateDeleteValidateModel<TTaskResult, TSourceObj, TMutateObj> model)
        where TTaskResult : class
        where TSourceObj : class
        where TMutateObj : class
    {
        TaskResult<TTaskResult>? myTaskResult =
            ValidateMutateModelModelState<TTaskResult, TMutateObj>(model.MutateModel);

        if (!myTaskResult.Success && model.TaskResult != null)
        {
            myTaskResult.ApplyToTaskResult(model.TaskResult);
        }
    }

    public virtual TaskResult<TTaskResult> ValidateMutateModelModelState<TTaskResult, TMutateObj>(
        TMutateObj mutateModel)
        where TTaskResult : class
        where TMutateObj : class
    {
        TaskResult<TTaskResult>? taskResult = new();
        ValidationContext context = new(mutateModel, _serviceProvider, null);
        List<ValidationResult> results = new();
        var isValid = Validator.TryValidateObject(mutateModel, context, results, true);

        if (!isValid)
        {
            results = results.Select(x =>
            {
                foreach (var memberName in x.MemberNames)
                {
                    taskResult.AddError(memberName, x.ErrorMessage);
                }

                return x;
            }).ToList();
        }

        return taskResult;
    }
}