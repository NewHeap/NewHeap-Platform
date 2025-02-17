using Microsoft.EntityFrameworkCore.Storage;

namespace NewHeap.Platform.AspNet.Common.DAL;

public partial interface ITransaction : IDisposable
{
    Task RollbackAsync();

    Task CommitAsync();
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

    public async Task RollbackAsync()
    {
        await _transaction.RollbackAsync();
    }

    public async Task CommitAsync()
    {
        await _transaction.CommitAsync();
    }
}