using Microsoft.AspNetCore.Mvc.ModelBinding;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common.Extensions;
public static partial class ModelStateExtensions
{
    public static ModelStateDictionary WithResultErrors<T>(this ModelStateDictionary modelState, TaskResult<T> result)
    {
        result.ApplyToModelState(modelState);
        return modelState;
    }
}
