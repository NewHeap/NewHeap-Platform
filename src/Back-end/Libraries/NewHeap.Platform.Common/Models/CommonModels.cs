using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Linq.Expressions;

namespace NewHeap.Platform.Common.Models;

public partial class MutationResultManagerModel<T>
    where T : class
{
    public MutationResultManagerModel()
    {
        ErrorMessages = new List<ErrorMutationResultManagerModel>();
    }

    public T? Result { get; set; }
    public List<ErrorMutationResultManagerModel> ErrorMessages { get; set; }
}

public class ErrorMutationResultManagerModel
{
    public required string Key { get; set; }
    public required string ErrorMessage { get; set; }
}

public class TaskResult
{
    public class ResultItem
    {
        public string Name { get; set; }

        public List<string> ErrorMessages { get; } = new List<string>();
    }

    public bool Success { get; private set; } = true;

    public List<ResultItem> Results { get; } = new List<ResultItem>();

    public TaskResult AddError(string error)
    {
        AddError("", error);
        return this;
    }

    public TaskResult AddError(string error, params string[] errorMessages)
    {
        Success = false;

        var resultItem = Results.FirstOrDefault(x => x.Name == error);

        if (resultItem == null)
        {
            resultItem = new ResultItem() { Name = error };
            Results.Add(resultItem);
        }

        foreach (var errorMessage in errorMessages)
        {
            resultItem.ErrorMessages.Add(errorMessage);
        }

        return this;
    }

    public TaskResult WithKeylessError(string errorMessage)
    {
        AddError(string.Empty, errorMessage);
        return this;
    }

    public TaskResult WithError(string name, string errorMessage)
    {
        AddError(name, errorMessage);
        return this;
    }

    public void ApplyToTaskResult(TaskResult taskResult)
    {
        foreach (var result in Results)
        {
            foreach (var errorMessage in result.ErrorMessages)
            {
                taskResult.AddError(result.Name, errorMessage);
            }
        }
    }

    public void ApplyToModelState(Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelState)
    {
        foreach (var result in Results)
        {
            foreach (var errorMessage in result.ErrorMessages)
            {
                modelState.AddModelError(result.Name, errorMessage);
            }
        }
    }

    public static TaskResult Succeeded => new TaskResult();

    public static TaskResult Failed(string error) => new TaskResult().AddError(error);
    public static TaskResult Failed(string name, string error) => new TaskResult().AddError(name, error);

    public List<string> AllErrorMessages => Results.SelectMany(x => x.ErrorMessages).ToList();

}

public class TaskResult<T> : TaskResult
{
    public T Data { get; set; }

    public void AddError(Expression<Func<T, object>> selector, params string[] errorMessages)
    {
        var name = (selector.Body as MemberExpression
            ?? ((UnaryExpression)selector.Body).Operand as MemberExpression).Member.Name;

        AddError(name, errorMessages);
    }

    public static implicit operator TaskResult<T>(T data) => new TaskResult<T>() { Data = data };

    public TaskResult()
    {

    }
}

public class CreateUpdateDeleteValidateModel<TTaskResult, TSourceObj, TMutateObj>
    where TTaskResult : class
    where TSourceObj : class
    where TMutateObj : class
{
    public CreateUpdateDeleteValidateModel(CRUDActionType actionType)
    {
        ActionType = actionType;
    }

    public CRUDActionType ActionType { get; private set; }
    public required TaskResult<TTaskResult> TaskResult { get; set; }
    public TSourceObj? SourceModel { get; set; }
    public TMutateObj? MutateModel { get; set; }

    public Guid? UserId { get; set; }
}