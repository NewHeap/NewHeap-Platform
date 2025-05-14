using NewHeap.Platform.Common.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.Common.Extensions;
public static class CollectionRequestModelExtensions
{
    public static IQueryable<T> CollectionRequestModelSkipTake<T>(this IQueryable<T> q, BaseCollectionRequestModel requestModel)
    {
        return q
            .Skip((requestModel.Page - 1) * requestModel.ItemsPerPage)
            .Take(requestModel.ItemsPerPage);

    }

    public static IEnumerable<T> CollectionRequestModelSkipTake<T>(this IEnumerable<T> q, BaseCollectionRequestModel requestModel)
    {
        return q
            .Skip((requestModel.Page - 1) * requestModel.ItemsPerPage)
            .Take(requestModel.ItemsPerPage);
    }
}
