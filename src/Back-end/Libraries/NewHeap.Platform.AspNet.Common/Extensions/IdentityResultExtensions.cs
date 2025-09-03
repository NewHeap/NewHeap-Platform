using NewHeap.Platform.AspNet.Common.Utilities;
using NewHeap.Platform.Common.Models;

namespace Microsoft.AspNetCore.Identity;

public static class NhIdentityResultExtensions
{
    public static T ToTaskResult<T>(this IdentityResult identityResult, T result)
        where T : TaskResult
    {
        return NhIdentityResultUtil.IdentityResultToTaskResult(identityResult, result);
    }
}
