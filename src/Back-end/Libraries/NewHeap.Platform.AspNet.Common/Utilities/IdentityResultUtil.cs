using Microsoft.AspNetCore.Identity;
using NewHeap.Platform.Common.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Utilities;
public static class NhIdentityResultUtil
{
    public static T IdentityResultToTaskResult<T>(IdentityResult identityResult, T result)
        where T : TaskResult
    {
        if (!identityResult.Succeeded)
        {
            foreach (var error in identityResult.Errors)
            {
                result.AddError(error.Code, error.Description);
            }
        }

        return result;
    }

}
