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

    public T Result { get; set; }
    public List<ErrorMutationResultManagerModel> ErrorMessages { get; set; }
}

public class ErrorMutationResultManagerModel
{
    public string Key { get; set; }
    public string ErrorMessage { get; set; }
}

public class TaskResult<T>
{
    public bool Success { get; private set; } = true;

    public List<ResultItem> Results { get; } = new();

    public List<string> AllErrorMessages => Results.SelectMany(x => x.ErrorMessages).ToList();

    public T Data { get; set; }

    public void AddError(Expression<Func<T, object>> selector, params string[] errorMessages)
    {
        var name = (selector.Body as MemberExpression
                    ?? ((UnaryExpression)selector.Body).Operand as MemberExpression).Member.Name;

        AddError(name, errorMessages);
    }

    public void AddError(string name, params string[] errorMessages)
    {
        Success = false;

        ResultItem? resultItem = Results.FirstOrDefault(x => x.Name == name);

        if (resultItem == null)
        {
            resultItem = new ResultItem { Name = name };
            Results.Add(resultItem);
        }

        foreach (var errorMessage in errorMessages)
        {
            resultItem.ErrorMessages.Add(errorMessage);
        }
    }

    public void ApplyToTaskResult<T2>(TaskResult<T2> taskResult)
    {
        foreach (ResultItem? result in Results)
        {
            foreach (var errorMessage in result.ErrorMessages)
            {
                taskResult.AddError(result.Name, errorMessage);
            }
        }
    }

    public void ApplyToModelState(ModelStateDictionary modelState)
    {
        foreach (ResultItem? result in Results)
        {
            foreach (var errorMessage in result.ErrorMessages)
            {
                modelState.AddModelError(result.Name, errorMessage);
            }
        }
    }

    public class ResultItem
    {
        public string Name { get; set; }

        public List<string> ErrorMessages { get; } = new();
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
    public TaskResult<TTaskResult> TaskResult { get; set; }
    public TSourceObj SourceModel { get; set; }
    public TMutateObj MutateModel { get; set; }

    public Guid? UserId { get; set; }
}