using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.Common.Services;

public partial class ValidationService
{
    protected readonly IServiceProvider _serviceProvider;

    public ValidationService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void ValidateMutateModelModelState<TTaskResult, TSourceObj, TMutateObj>(CreateUpdateDeleteValidateModel<TTaskResult, TSourceObj, TMutateObj> model)
        where TTaskResult : class
        where TSourceObj : class
        where TMutateObj : class
    {
        var myTaskResult = ValidateMutateModelModelState<TTaskResult, TMutateObj>(model.MutateModel);

        if (!myTaskResult.Success && model.TaskResult != null)
        {
            myTaskResult.ApplyToTaskResult(model.TaskResult);
        }
    }

    public TaskResult<TTaskResult> ValidateMutateModelModelState<TTaskResult, TMutateObj>(TMutateObj mutateModel)
        where TTaskResult : class
        where TMutateObj : class
    {
        var taskResult = new TaskResult<TTaskResult>();
        var context = new System.ComponentModel.DataAnnotations.ValidationContext(mutateModel, serviceProvider: _serviceProvider, items: null);
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(mutateModel, context, results, validateAllProperties: true);

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
