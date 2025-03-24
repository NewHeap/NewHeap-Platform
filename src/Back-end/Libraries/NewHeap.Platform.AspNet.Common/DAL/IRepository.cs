using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.DAL;
public interface IRepository<T> where T : class
{
    NhIdentityDbContext Context { get; }

    void Add(T entity);
    void Add<TEntity>(TEntity entity) where TEntity : class;
    ValueTask<EntityEntry<T>> AddAsync(T entity);
    ValueTask<EntityEntry<TEntity>> AddAsync<TEntity>(TEntity entity, CancellationToken cancellationToken = default) where TEntity : class;
    void AddRange(IEnumerable<T> entities);
    void AddRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;
    Task AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);
    Task AddRangeAsync<TEntity>(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default) where TEntity : class;
    bool Any(Expression<Func<T, bool>> predicate);
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    void ClearTracking();
    CollectionEntry<TEntity, TProperty> Collection<TEntity, TProperty>(TEntity entity, Expression<Func<TEntity, IEnumerable<TProperty>>> propertyExpression)
        where TEntity : class
        where TProperty : class;
    CollectionEntry Collection<TEntity, TProperty>(TEntity entity, string propertyName)
        where TEntity : class
        where TProperty : class;
    Task ConfirmCollectionLoaded<TEntity, TProperty>(TEntity entity, Expression<Func<TEntity, IEnumerable<TProperty>>> propertyExpression, CancellationToken cancellationToken = default)
        where TEntity : class
        where TProperty : class;
    Task ConfirmLoaded<TEntity, TProperty>(TEntity entity, Expression<Func<TEntity, TProperty>> propertyExpression, CancellationToken cancellationToken = default)
        where TEntity : class
        where TProperty : class;
    T? Find<TKey>(TKey id);
    ValueTask<T?> FindAsync<TKey>(TKey id, CancellationToken cancellationToken = default);
    IQueryable<T> FindBy(Expression<Func<T, bool>> predicate);
    T? FindOneBy(Expression<Func<T, bool>> predicate);
    Task<T?> FindOneByAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);
    IQueryable<T> GetAll();
    DbSet<TDbSet> GetDbSet<TDbSet>() where TDbSet : class;
    IRepository<T> NewScope();
    ReferenceEntry<TEntity, TProperty> Reference<TEntity, TProperty>(TEntity entity, Expression<Func<TEntity, TProperty>> propertyExpression)
        where TEntity : class
        where TProperty : class;
    ReferenceEntry Reference<TEntity, TProperty>(TEntity entity, string propertyName)
        where TEntity : class
        where TProperty : class;
    void Remove(T entity);
    void Remove<TEntity>(TEntity entity) where TEntity : class;
    void RemoveRange(IEnumerable<T> entities);
    void RemoveRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;
    int SaveChanges();
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    Task<ITransaction> StartTransactionAsync(CancellationToken cancellationToken = default);
    void Update(T entity);
    void Update<TEntity>(TEntity entity) where TEntity : class;
    void UpdateRange(IEnumerable<T> entities);
    void UpdateRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;
}