using NewHeap.Platform.Common.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.Common.Exceptions;

public class TaskResultException : Exception
{
    public TaskResult? Result { get; }

    public TaskResultException(TaskResult? result)
        : base(BuildMessage(result))
    {
        Result = result;
    }

    protected static string BuildMessage(TaskResult? result)
    {
        if(result == null)
        {
            return "TaskResult is null.";
        }

        var items = result.GetResultItems();
        var sb = new StringBuilder();
        sb.AppendLine($"TaskResult Success = {result.Success}");

        foreach (var item in items)
        {
            var key = string.IsNullOrEmpty(item.Name) ? "(global)" : item.Name;
            foreach (var msg in item.ErrorMessages)
            {
                sb.AppendLine($"• {key}: {msg}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}

public class TaskResultException<T> : TaskResultException
{
    public new TaskResult<T>? Result { get; }

    public TaskResultException(TaskResult<T>? result)
        : base(result)
    {
        Result = result;
    }
}
