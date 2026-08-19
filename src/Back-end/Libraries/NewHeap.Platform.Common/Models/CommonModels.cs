using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Linq;
using Microsoft.Extensions.Localization;
using System.Diagnostics.CodeAnalysis;
using NewHeap.Platform.Common.Utilities;

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

public partial class ErrorMutationResultManagerModel
{
    public required string Key { get; set; }
    public required string ErrorMessage { get; set; }
}

public partial class TaskResult
{
    public class ResultItem
    {
        public string Name { get; set; } = "";

        public List<FormattableString> ErrorMessages { get; } = [];
    }

    public virtual bool Success { get; protected set; } = true;

    protected List<ResultItem> Results { get; } = [];

    public List<ResultItem> GetResultItems() => Results;

    protected FormattableString CreateFormattableString(string format, object[]? args = null)
    {
        return SafeFormattableStringFactory.Create(format ?? "", args ?? []);
    }

    public virtual TaskResult AddError(string error)
    {
        AddError(CreateFormattableString(error));
        return this;
    }

    public virtual TaskResult AddError(FormattableString error)
    {
        AddError(string.Empty, error);
        return this;
    }

    public virtual TaskResult AddError(string name, IEnumerable<string> errorMessages)
    {
        AddError(name, errorMessages.Select(x => CreateFormattableString(x)));
        return this;
    }

    public virtual TaskResult AddError(string name, IEnumerable<FormattableString> errorMessages)
    {
        foreach (var errorMessage in errorMessages)
        {
            AddError(name, errorMessage);
        }

        return this;
    }

    public virtual TaskResult AddError(string name, params string[] errorMessages)
    {
        AddError(name, errorMessages.Select(x => CreateFormattableString(x)));
        return this;
    }

    public virtual TaskResult AddError(string name, params FormattableString[] errorMessages)
    {
        Success = false;

        var resultItem = Results.FirstOrDefault(x => x.Name == name);

        if (resultItem == null)
        {
            resultItem = new ResultItem() { Name = name };
            Results.Add(resultItem);
        }

        foreach (var errorMessage in errorMessages)
        {
            resultItem.ErrorMessages.Add(errorMessage);
        }

        return this;
    }

    public virtual TaskResult WithKeylessError(string errorMessage)
    {
        AddError(CreateFormattableString(errorMessage));
        return this;
    }

    public virtual TaskResult WithKeylessError(FormattableString errorMessage)
    {
        AddError(string.Empty, errorMessage);
        return this;
    }

    public virtual TaskResult WithError(string name, string errorMessage)
    {
        AddError(name, CreateFormattableString(errorMessage));
        return this;
    }

    public virtual TaskResult WithError(string name, FormattableString errorMessage)
    {
        AddError(name, errorMessage);
        return this;
    }

    public void ApplyTo(TaskResult taskResult) => ApplyToTaskResult(taskResult);
    public void ApplyTo(ModelStateDictionary modelState) => ApplyToModelState(modelState);

    public virtual void ApplyToTaskResult(TaskResult taskResult)
    {
        foreach (var result in Results)
        {
            foreach (var errorMessage in result.ErrorMessages)
            {
                taskResult.AddError(result.Name, errorMessage);
            }
        }
    }

    public virtual void ApplyToModelState(ModelStateDictionary modelState, IStringLocalizer? stringLocalizer = null)
    {
        foreach (var result in Results)
        {
            foreach (var errorMessage in result.ErrorMessages)
            {
                var errorString = stringLocalizer != null 
                    ? stringLocalizer[errorMessage.Format, (errorMessage.GetArguments() ?? []).Select(x => x == null ? "" : x)] 
                    : errorMessage.ToString();

                modelState.AddModelError(result.Name, errorMessage.ToString());
            }
        }
    }

    public virtual TaskResult ApplyModelStateErrors(Dictionary<string, string[]> errors)
    {
        foreach (var (key, value) in errors)
        {
            var values = value.Select(x => CreateFormattableString(x)).ToArray();
            AddError(key, values);
        }

        return this;
    }

    public virtual TaskResult ApplyModelStateErrors(Dictionary<string, FormattableString[]> errors)
    {
        foreach (var (key, value) in errors)
        {
            AddError(key, value);
        }

        return this;
    }

    public static TaskResult Succeeded() => new TaskResult();
    public static TaskResult Succeeded(TaskResult taskResult)
    {
        var result = new TaskResult();
        taskResult?.ApplyToTaskResult(result);

        return result;
    }

    public static TaskResult Failed(string error) => new TaskResult().AddError(error);
    public static TaskResult Failed(FormattableString error) => new TaskResult().AddError(error);
    public static TaskResult Failed(string name, FormattableString error) => new TaskResult().AddError(name, error);
    public static TaskResult Failed(string name, string error) => new TaskResult().AddError(name, error);
    public static TaskResult Failed(TaskResult taskResult)
    { 
        var taskResult1 = new TaskResult();
        taskResult.ApplyToTaskResult(taskResult1);

        if(taskResult.Success)
        {
            taskResult1.Success = false;
        }

        return taskResult1;
    }

    public List<FormattableString> AllErrorMessages => Results.SelectMany(x => x.ErrorMessages.Select(s => string.IsNullOrWhiteSpace(x.Name) ? s : $"'{x.Name}': {s.ToString()}")).ToList();

}

public partial class TaskResult<T> : TaskResult
{
    [MemberNotNullWhen(true,nameof(Data))]
    public override bool Success { get; protected set; } = true;

    public T? Data { get; set; }

    public TaskResult<T> AddError(Expression<Func<T, object>> selector, params string[] errorMessages)
    {
        AddError(selector, errorMessages.Select(x => CreateFormattableString(x)).ToArray());
        return this;
    }

    public TaskResult<T> AddError(Expression<Func<T, object>> selector, params FormattableString[] errorMessages)
    {
        var name = (selector.Body as MemberExpression
            ?? ((UnaryExpression)selector.Body)!.Operand as MemberExpression)!.Member.Name;

        AddError(name, errorMessages);
        return this;
    }

    public static TaskResult<T> Succeeded(T data) => new TaskResult<T> { Data = data };
    public static TaskResult<T> Succeeded(TaskResult<T> data)
    { 
        var result = new TaskResult<T>();
        result.Data = data.Data;

        return result;
    }

    public static implicit operator TaskResult<T>(T data) => new TaskResult<T>() { Data = data };

    public static new TaskResult<T> Failed(string error)
    {
        var r = new TaskResult<T>();
        r.AddError(error);
        return r;
    }

    public static new TaskResult<T> Failed(FormattableString error)
    {
        var r = new TaskResult<T>();
        r.AddError(error);
        return r;
    }

    public static new TaskResult<T> Failed(string name, string error)
    {
        var r = new TaskResult<T>();
        r.AddError(name, error);
        return r;
    }

    public static new TaskResult<T> Failed(string name, FormattableString error)
    {
        var r = new TaskResult<T>();
        r.AddError(name, error);
        return r;
    }

    public static new TaskResult<T> Failed(TaskResult taskResult)
    {
        var taskResult1 = new TaskResult<T>();
        taskResult.ApplyToTaskResult(taskResult1);

        if (taskResult.Success)
        {
            taskResult1.Success = false;
        }

        return taskResult1;
    }

    public static TaskResult<T> Failed(TaskResult<T> taskResult)
    {
        var taskResult1 = new TaskResult<T>();
        taskResult.ApplyToTaskResult(taskResult1);

        if (taskResult.Success)
        {
            taskResult1.Success = false;
        }

        return taskResult1;
    }

    public new TaskResult<T> WithKeylessError(string errorMessage)
    {
        return WithKeylessError(CreateFormattableString(errorMessage));
    }

    public new TaskResult<T> WithKeylessError(FormattableString errorMessage)
    {
        AddError(string.Empty, errorMessage);
        return this;
    }

    public new TaskResult<T> WithError(string name, string errorMessage)
    {
        return WithError(name, CreateFormattableString(errorMessage));
    }

    public new TaskResult<T> WithError(string name, FormattableString errorMessage)
    {
        AddError(name, errorMessage);
        return this;
    }

    public TaskResult<T> WithError(Expression<Func<T, object>> selector, params string[] errorMessages)
    { 
        return WithError(selector, errorMessages.Select(x => CreateFormattableString(x)).ToArray());
    }

    public TaskResult<T> WithError(Expression<Func<T, object>> selector, params FormattableString[] errorMessages)
    {
        AddError(selector, errorMessages);
        return this;
    }

    public TaskResult<T> AddError(string[] errorMessages)
    { 
        return AddError(errorMessages.Select(x => CreateFormattableString(x)).ToArray());
    }

    public TaskResult<T> AddError(FormattableString[] errorMessages)
    {
        AddError(string.Empty, errorMessages);
        return this;
    }

    public new TaskResult<T> AddError(string name, IEnumerable<string> errorMessages)
    { 
        return AddError(name, errorMessages.Select(x => CreateFormattableString(x)));
    }

    public new TaskResult<T> AddError(string name, IEnumerable<FormattableString> errorMessages)
    {
        foreach (var errorMessage in errorMessages)
        {
            AddError(name, errorMessage);
        }

        return this;
    }

    public TaskResult<T> AddError(string name, string errorMessage)
    { 
        return AddError(name, CreateFormattableString(errorMessage));
    }

    public TaskResult<T> AddError(string name, FormattableString errorMessage)
    {
        Success = false;

        var resultItem = Results.FirstOrDefault(x => x.Name == name);

        if (resultItem == null)
        {
            resultItem = new ResultItem() { Name = name };
            Results.Add(resultItem);
        }

        resultItem.ErrorMessages.Add(errorMessage);
        return this;
    }

    public new TaskResult<T> ApplyModelStateErrors(Dictionary<string, string[]> errors)
    { 
        var newDict = new Dictionary<string, FormattableString[]>();
        newDict = errors.ToDictionary(x => x.Key, x => x.Value.Select(y => CreateFormattableString(y)).ToArray());

        return ApplyModelStateErrors(newDict);
    }

    public new TaskResult<T> ApplyModelStateErrors(Dictionary<string, FormattableString[]> errors)
    {
        foreach (var (key, value) in errors)
        {
            AddError(key, value);
        }

        return this;
    }

    public TaskResult<T2> ApplyToTaskResult<T2>(TaskResult<T2> taskResult)
    {
        foreach (var result in Results)
        {
            foreach (var errorMessage in result.ErrorMessages)
            {
                taskResult.AddError(result.Name, errorMessage);
            }
        }

        return taskResult;
    }

    public TaskResult<T> WithData(T value)
    {
        Data = value;
        return this;
    }
}

public partial class DisposableTaskResult<T> : TaskResult<T>, IDisposable where T : IDisposable
{
    public void Dispose()
    {
        Data?.Dispose();
    }

    public static implicit operator DisposableTaskResult<T>(T data) => new DisposableTaskResult<T> { Data = data };

    public static new DisposableTaskResult<T> Failed(string name, string error)
    {
        var r = new DisposableTaskResult<T>();
        r.AddError(name, error);
        return r;
    }

    public static new DisposableTaskResult<T> Failed(string name, FormattableString error)
    {
        var r = new DisposableTaskResult<T>();
        r.AddError(name, error);
        return r;
    }

    public static new DisposableTaskResult<T> Failed(string error)
    {
        var r = new DisposableTaskResult<T>();
        r.AddError(error);
        return r;
    }

    public static new DisposableTaskResult<T> Failed(FormattableString error)
    {
        var r = new DisposableTaskResult<T>();
        r.AddError(error);
        return r;
    }
}

public partial class CreateUpdateDeleteValidateModel<TTaskResult, TSourceObj, TMutateObj>
    where TTaskResult : class?
    where TSourceObj : class?
    where TMutateObj : class?
{
    public CreateUpdateDeleteValidateModel(CRUDActionType actionType)
    {
        ActionType = actionType;
    }

    public CRUDActionType ActionType { get; private set; }
    public required TaskResult<TTaskResult?> TaskResult { get; set; }
    public TSourceObj? SourceModel { get; set; }
    public TMutateObj? MutateModel { get; set; }

    public Guid? UserId { get; set; }
}