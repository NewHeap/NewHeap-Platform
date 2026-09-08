using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.Common.Models;

namespace NewHeap.Platform.AspNet.Common;

public static class TaskResultExtensions
{
    /// <summary>
    /// Copies result errors to the HTTP model state without changing the result.
    /// </summary>
    public static void ApplyTo(this TaskResult result, ModelStateDictionary modelState)
    {
        result.ApplyToModelState(modelState);
    }

    /// <summary>
    /// Adapts a provider-independent result to ASP.NET model validation.
    /// </summary>
    public static void ApplyToModelState(
        this TaskResult result,
        ModelStateDictionary modelState,
        IStringLocalizer? stringLocalizer = null)
    {
        foreach (var item in result.GetResultItems())
        {
            foreach (var errorMessage in item.ErrorMessages)
            {
                // Preserve the legacy adapter's lookup and original message formatting.
                // Localization behavior is independent of moving the ASP.NET boundary.
                if (stringLocalizer is not null)
                {
                    _ = stringLocalizer[errorMessage.Format,
                        (errorMessage.GetArguments() ?? []).Select(x => x ?? "")];
                }

                modelState.AddModelError(item.Name, errorMessage.ToString());
            }
        }
    }

}
