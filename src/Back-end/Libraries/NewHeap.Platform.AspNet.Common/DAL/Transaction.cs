using Microsoft.EntityFrameworkCore.Storage;

namespace NewHeap.Platform.AspNet.Common.DAL;

public partial interface ITransaction : IDisposable, IAsyncDisposable
{
    Task RollbackAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);
}

public partial class Transaction : ITransaction
{
    private readonly IDbContextTransaction _transaction;

    internal Transaction(IDbContextTransaction transaction)
    {
        _transaction = transaction;
    }

    public void Dispose()
    {
        _transaction.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _transaction.DisposeAsync();
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        await _transaction.RollbackAsync(cancellationToken);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        await _transaction.CommitAsync(cancellationToken);
    }
}