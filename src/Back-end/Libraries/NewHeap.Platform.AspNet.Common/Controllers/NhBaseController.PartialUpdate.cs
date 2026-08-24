using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.Common.Utilities;
using Newtonsoft.Json.Linq;

namespace NewHeap.Platform.AspNet.Common.Controllers;

public abstract partial class NhBaseController
{
    /// <summary>
    /// Applies a top-level partial JSON object to an existing model and validates the complete result.
    /// </summary>
    /// <remarks>
    /// Pass an isolated mutate model rather than a tracked entity. Mapping errors leave the model unchanged;
    /// model-validation errors leave the attempted values applied so the caller can inspect the rejected model.
    /// </remarks>
    [NonAction]
    protected bool TryApplyPartialUpdate<TModel>(
        TModel target,
        JObject? partialUpdate,
        Func<string, bool>? canPartiallyUpdateProperty = null)
        where TModel : class
    {
        ArgumentNullException.ThrowIfNull(target);
        canPartiallyUpdateProperty ??= static _ => true;

        if (!NhPartialUpdateControllerExecutor.TryCreateMapping(
                this,
                _localizer,
                partialUpdate,
                canPartiallyUpdateProperty,
                out NhPartialUpdateMapping<TModel> mapping))
        {
            return false;
        }

        mapping.Apply(new NhSetPropertyCalls<TModel>()).Apply(target);
        return TryValidateModel(target);
    }
}
