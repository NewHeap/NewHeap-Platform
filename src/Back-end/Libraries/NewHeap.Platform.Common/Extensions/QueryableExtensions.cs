using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace NewHeap.Platform.Common;
public static partial class QueryableExtensions
{
    public static IQueryable<T> WhereIf<T>(this IQueryable<T> query, bool condition, Expression<Func<T, bool>> predicate)
    {
        return condition
            ? query.Where(predicate)
            : query;
    }

    public static IQueryable<T> IncludeIf<T, TProperty>(this IQueryable<T> query, bool condition, Expression<Func<T, TProperty>> navigationPropertyPath) where T : class
    {
        return condition
            ? query.Include(navigationPropertyPath)
            : query;
    }
}
