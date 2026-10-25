using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Extensions;
using System.Data;
using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.AspNet.Common.DAL;

public partial class Repository<T> : IRepository<T> 
    where T : class
{
    private readonly IServiceProvider _serviceProvider;

    public DebugView DebugView => Context.ChangeTracker.DebugView;
    
    protected readonly DbSet<T> DbSet;

    public string TableName { get; }
    
    public Repository(DbContext context, IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
        Context = context;
        DbSet = context.Set<T>();
        TableName = TableFor<T>().Value; // We want this to blow up ASAP if the entity is not mapped to a table.
    }

    public DbContext Context { get; }

    public DbSet<TDbSet> GetDbSet<TDbSet>()
        where TDbSet : class
    {
        return Context.Set<TDbSet>();
    }

    public RawSqlString TableFor<V>() => Context.Model.Table<V>().Raw();
    
    public RawSqlString ColumnFor<V>(Expression<Func<V, object?>> prop, bool prefixTable = true) => Context.Model.Column(prop, prefixTable).Raw();
    
    public virtual CollectionEntry<TEntity, TProperty> Collection<TEntity, TProperty>(TEntity entity,
        Expression<Func<TEntity, IEnumerable<TProperty>>> propertyExpression)
        where TProperty : class
        where TEntity : class
    {
        return Context.Entry(entity).Collection(propertyExpression);
    }

    public virtual CollectionEntry Collection<TEntity, TProperty>(TEntity entity, string propertyName)
        where TProperty : class
        where TEntity : class
    {
        return Context.Entry(entity).Collection(propertyName);
    }

    public virtual ReferenceEntry<TEntity, TProperty> Reference<TEntity, TProperty>(TEntity entity,
        Expression<Func<TEntity, TProperty>> propertyExpression)
        where TProperty : class
        where TEntity : class
    {
        return Context.Entry(entity).Reference(propertyExpression!);
    }

    public virtual ReferenceEntry Reference<TEntity, TProperty>(TEntity entity, string propertyName)
        where TProperty : class
        where TEntity : class
    {
        return Context.Entry(entity).Reference(propertyName);
    }

    public virtual async Task ConfirmLoaded<TEntity, TProperty>(TEntity entity,
        Expression<Func<TEntity, TProperty>> propertyExpression, CancellationToken cancellationToken = default)
        where TProperty : class
        where TEntity : class
    {
        ReferenceEntry<TEntity, TProperty>? reference = Reference(entity, propertyExpression);
        if (!reference.IsLoaded)
        {
            await reference.LoadAsync(cancellationToken);
        }
    }

    public virtual async Task ConfirmCollectionLoaded<TEntity, TProperty>(TEntity entity,
        Expression<Func<TEntity, IEnumerable<TProperty>>> propertyExpression,
        CancellationToken cancellationToken = default)
        where TProperty : class
        where TEntity : class
    {
        CollectionEntry<TEntity, TProperty>? collection = Context.Entry(entity).Collection(propertyExpression);
        if (!collection.IsLoaded)
        {
            await collection.LoadAsync(cancellationToken);
        }
    }

    public virtual void Add(T entity)
    {
        DbSet.Add(entity);
    }

    public virtual void Add<TEntity>(TEntity entity)
        where TEntity : class
    {
        Context.Set<TEntity>().Add(entity);
    }

    public virtual ValueTask<EntityEntry<T>> AddAsync(T entity)
    {
        return DbSet.AddAsync(entity);
    }

    public virtual ValueTask<EntityEntry<TEntity>> AddAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        return Context.Set<TEntity>().AddAsync(entity, cancellationToken);
    }

    public virtual void AddRange(IEnumerable<T> entities)
    {
        DbSet.AddRange(entities);
    }

    public virtual void AddRange<TEntity>(IEnumerable<TEntity> entities)
        where TEntity : class
    {
        Context.Set<TEntity>().AddRange(entities);
    }

    public virtual Task AddRangeAsync<TEntity>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        return Context.Set<TEntity>().AddRangeAsync(entities, cancellationToken);
    }

    public virtual T? Find<TKey>(TKey id)
    {
        return DbSet.Find(id);
    }

    public virtual ValueTask<T?> FindAsync<TKey>(TKey id, CancellationToken cancellationToken = default)
    {
        return DbSet.FindAsync(id, cancellationToken);
    }

    public virtual T? FindOneBy(Expression<Func<T, bool>> predicate)
    {
        return DbSet.FirstOrDefault(predicate);
    }

    public virtual Task<T?> FindOneByAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
    {
        return DbSet.FirstOrDefaultAsync(predicate, cancellationToken);
    }

    public virtual IQueryable<T> FindBy(Expression<Func<T, bool>> predicate)
    {
        return DbSet.Where(predicate);
    }

    public virtual bool Any(Expression<Func<T, bool>> predicate)
    {
        return DbSet.Any(predicate);
    }

    public virtual Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default)
    {
        return DbSet.AnyAsync(predicate, cancellationToken);
    }

    public virtual IQueryable<T> GetAll()
    {
        return DbSet;
    }

    public virtual void Update(T entity)
    {
        Context.Entry(entity).State = EntityState.Modified;
    }

    public virtual void Update<TEntity>(TEntity entity)
        where TEntity : class
    {
        Context.Set<TEntity>().Update(entity);
    }

    public virtual void UpdateRange(IEnumerable<T> entities)
    {
        DbSet.UpdateRange(entities);
    }

    public virtual void UpdateRange<TEntity>(IEnumerable<TEntity> entities)
        where TEntity : class
    {
        Context.Set<TEntity>().UpdateRange(entities);
    }

    public virtual void Remove(T entity)
    {
        DbSet.Remove(entity);
    }

    public virtual void Remove<TEntity>(TEntity entity)
        where TEntity : class
    {
        Context.Set<TEntity>().Remove(entity);
    }

    public virtual void RemoveRange(IEnumerable<T> entities)
    {
        DbSet.RemoveRange(entities);
    }

    public virtual void RemoveRange<TEntity>(IEnumerable<TEntity> entities)
        where TEntity : class
    {
        Context.Set<TEntity>().RemoveRange(entities);
    }

    public void ClearTracking()
    {
        Context.ChangeTracker.Clear();
    }

    public IRepository<T> NewScope()
    {
        var type = Context.GetType();
        var opt = type.GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Context);
        var context = (DbContext)Activator.CreateInstance(type, opt)!;
        return new Repository<T>(context, _serviceProvider);
    }

    /// <summary>
    /// Opens a transaction on the repository's context through the registered transaction factories.
    /// </summary>
    /// <remarks>
    /// The registered <see cref="NhTransactionFactory"/> delegates run first, in registration order,
    /// and then the registered <see cref="NhTransactionFactoryAsync"/> delegates, in registration
    /// order. Each factory receives the transaction the factory before it returned, or <c>null</c>.
    /// Without any factory, or when the factories return none, the context opens the transaction with
    /// <c>BeginTransactionAsync</c>. A cancelled token stops the call before anything opens. When a
    /// factory fails or the call is cancelled after an earlier factory returned a transaction, that
    /// transaction is disposed before the exception propagates, so the context holds no transaction.
    /// </remarks>
    public async Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var transaction = await OpenTransactionThroughFactoriesAsync(cancellationToken);
        if (transaction == null)
        {
            transaction = await Context.Database.BeginTransactionAsync(cancellationToken);
        }

        return new Transaction(transaction);
    }

    private async Task<IDbContextTransaction?> OpenTransactionThroughFactoriesAsync(CancellationToken cancellationToken)
    {
        var factories = _serviceProvider.GetServices<NhTransactionFactory>();
        var asyncFactories = _serviceProvider.GetServices<NhTransactionFactoryAsync>();

        IDbContextTransaction? transaction = null;
        try
        {
            foreach (var factory in factories)
            {
                transaction = factory(transaction, Context, _serviceProvider);
            }

            foreach (var factory in asyncFactories)
            {
                transaction = await factory(transaction, Context, _serviceProvider, cancellationToken);
            }
        }
        catch
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }

            throw;
        }

        return transaction;
    }

    /// <summary>
    /// Create a transaction scope with support for inner transactions, will only commit if we own the transaction. (First creator)
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<INhDbTransactionScope> StartOrGetTransactionScopeAsync(CancellationToken cancellationToken = default)
    {
        IDbContextTransaction? tx = Context?.Database?.CurrentTransaction;
        if (tx == null)
        {
            var transaction = await StartTransactionAsync(cancellationToken);
            return new NhDbTransactionScope(
                transaction: transaction!,
                isMyTransaction: true
            );
        }
        else
        {
            return new NhDbTransactionScope(
                transaction: new Transaction(tx),
                isMyTransaction: false
            );
        }
    }

    public virtual async Task<bool> TryAcquireTransactionLockAsync(
        INhDbTransactionScope transactionScope,
        string resourceName,
        int lockTimeoutInMilliseconds = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactionScope);

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            throw new ArgumentException("The lock resource name is required.", nameof(resourceName));
        }

        resourceName = resourceName.Length <= 255
            ? resourceName
            : resourceName[..255];

        var lockProvider = NhRepositoryProvider.GetRequired(Context);
        var connection = Context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        return await lockProvider.TryAcquireTransactionLockAsync(
            connection,
            transactionScope.Transaction.DbContextTransaction.GetDbTransaction(),
            resourceName,
            Math.Max(0, lockTimeoutInMilliseconds),
            cancellationToken);
    }

    public virtual int SaveChanges()
    {
        return Context.SaveChanges();
    }

    public virtual Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return Context.SaveChangesAsync(cancellationToken);
    }

    public virtual Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default)
    {
        return DbSet.AddRangeAsync(entities, cancellationToken);
    }
}

public interface INhDbTransactionScope : IDisposable, IAsyncDisposable
{
    bool IsMyTransaction { get; init; }

    ITransaction Transaction { get; }

    /// <summary>
    /// Only rolls back if we own the transaction.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task RollbackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Only commits if we own the transaction. 
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task CommitAsync(CancellationToken cancellationToken = default);
}

public class NhDbTransactionScope : INhDbTransactionScope
{
    public bool IsMyTransaction { get; init; } = false;

    public ITransaction Transaction { get; init; } = null!;

    public NhDbTransactionScope(ITransaction transaction, bool isMyTransaction)
    {
        Transaction = transaction;
        IsMyTransaction = isMyTransaction;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    { 
        if(IsMyTransaction)
        {
            await Transaction.RollbackAsync(cancellationToken);
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (IsMyTransaction)
        {
            await Transaction.CommitAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        if (IsMyTransaction)
        {
            Transaction.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsMyTransaction)
        {
            await Transaction.DisposeAsync();
        }
    }
}

/// <summary>
/// Opens or wraps the transaction that <see cref="Repository{T}.StartTransactionAsync"/> starts,
/// synchronously. Kept for compatibility; prefer <see cref="NhTransactionFactoryAsync"/>.
/// </summary>
/// <remarks>
/// Synchronous factories run before the asynchronous ones. Each receives the transaction the factory
/// before it returned (<paramref name="prev"/>), or <c>null</c> when it is the first, and returns the
/// transaction the next factory receives.
/// </remarks>
/// <param name="prev">The transaction the factory before this one returned, or <c>null</c>.</param>
/// <param name="dbContext">The repository's context.</param>
/// <param name="serviceProvider">The repository's service provider.</param>
public delegate IDbContextTransaction NhTransactionFactory(IDbContextTransaction? prev,DbContext dbContext,IServiceProvider serviceProvider);

/// <summary>
/// Opens or wraps the transaction that <see cref="Repository{T}.StartTransactionAsync"/> starts,
/// asynchronously and cancellation-aware. Register it as a singleton
/// <c>NhTransactionFactoryAsync</c> service.
/// </summary>
/// <remarks>
/// Asynchronous factories run after the synchronous <see cref="NhTransactionFactory"/> delegates, in
/// registration order. Each receives the transaction the factory before it returned
/// (<paramref name="prev"/>), or <c>null</c> when no factory returned one yet, and then opens the
/// transaction itself, preferably with <c>BeginTransactionAsync(cancellationToken)</c>, or wraps
/// <paramref name="prev"/>; the transaction it returns owns <paramref name="prev"/> from then on.
/// When a factory throws, including for cancellation, the repository disposes the transaction the
/// factory received, so a failing factory disposes only a transaction it opened itself.
/// </remarks>
/// <param name="prev">The transaction the factory before this one returned, or <c>null</c>.</param>
/// <param name="dbContext">The repository's context.</param>
/// <param name="serviceProvider">The repository's service provider.</param>
/// <param name="cancellationToken">The caller's token of <c>StartTransactionAsync</c>.</param>
public delegate Task<IDbContextTransaction> NhTransactionFactoryAsync(
    IDbContextTransaction? prev,
    DbContext dbContext,
    IServiceProvider serviceProvider,
    CancellationToken cancellationToken);
