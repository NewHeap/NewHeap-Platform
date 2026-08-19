using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.Common.Extensions;

public static class EFCoreBatchExtensions
{
    public static async IAsyncEnumerable<List<T>> ChunkAsync<T>(
        this IQueryable<T> query,
        int chunkSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize));

        var page = 0;

        while (true)
        {
            var batch = await query
                .Skip(page * chunkSize)
                .Take(chunkSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
                yield break;

            yield return batch;
            page++;
        }
    }
}