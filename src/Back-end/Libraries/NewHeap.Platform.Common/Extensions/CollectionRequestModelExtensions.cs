using NewHeap.Platform.Common.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.Common.Extensions;
public static class CollectionRequestModelExtensions
{
    public static List<T> PageSkipTake<T>(this List<T> q, int page, int itemsPerPage)
    {
        if(page < 1)
        {
            page = 1;
        }

        if (itemsPerPage < 0)
        {
            itemsPerPage = 0;
        }

        return q
            .Skip((page - 1) * itemsPerPage)
            .Take(itemsPerPage)
            .ToList();
    }

    public static List<T> PageSkipTake<T>(this List<T> q, IBaseCollectionRequestModel requestModel)
    {
        return q.PageSkipTake(requestModel.Page, requestModel.ItemsPerPage);
    }

    public static IQueryable<T> PageSkipTake<T>(this IQueryable<T> q, int page, int itemsPerPage)
    {
        if (page < 1)
        {
            page = 1;
        }

        if (itemsPerPage < 0)
        {
            itemsPerPage = 0;
        }

        return q
            .Skip((page - 1) * itemsPerPage)
            .Take(itemsPerPage);
    }

    public static IQueryable<T> PageSkipTake<T>(this IQueryable<T> q, IBaseCollectionRequestModel requestModel)
    {
        return q.PageSkipTake(requestModel.Page, requestModel.ItemsPerPage);
    }
}
