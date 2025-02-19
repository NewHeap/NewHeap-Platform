using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Linq.Expressions;

namespace NewHeap.Platform.AspNet.Common.DAL;

public partial interface IRepository<T> where T : class
{
    NhDbContext Context { get; }
    DbSet<TDbSet> GetDbSet<TDbSet>() where TDbSet : class;

    CollectionEntry<TEntity, TProperty> Collection<TEntity, TProperty>(TEntity entity,
        Expression<Func<TEntity, IEnumerable<TProperty>>> propertyExpression)
        where TProperty : class
        where TEntity : class;

    CollectionEntry Collection<TEntity, TProperty>(TEntity entity, string propertyName)
        where TProperty : class
        where TEntity : class;

    ReferenceEntry<TEntity, TProperty> Reference<TEntity, TProperty>(TEntity entity,
        Expression<Func<TEntity, TProperty>> propertyExpression)
        where TProperty : class
        where TEntity : class;

    ReferenceEntry Reference<TEntity, TProperty>(TEntity entity, string propertyName)
        where TProperty : class
        where TEntity : class;

    Task ConfirmLoaded<TEntity, TProperty>(TEntity entity, Expression<Func<TEntity, TProperty>> propertyExpression,
        CancellationToken cancellationToken = default)
        where TProperty : class
        where TEntity : class;

    Task ConfirmCollectionLoaded<TEntity, TProperty>(TEntity entity,
        Expression<Func<TEntity, IEnumerable<TProperty>>> propertyExpression,
        CancellationToken cancellationToken = default)
        where TProperty : class
        where TEntity : class;

    T Find<TKey>(TKey id);
    ValueTask<T> FindAsync<TKey>(TKey id);
    T FindOneBy(Expression<Func<T, bool>> predicate);
    Task<T> FindOneByAsync(Expression<Func<T, bool>> predicate);
    bool Any(Expression<Func<T, bool>> predicate);
    Task<bool> AnyAsync(Expression<Func<T, bool>> predicate);
    IQueryable<T> GetAll();

    IQueryable<T> FindBy(Expression<Func<T, bool>> predicate);
    int SaveChanges();
    Task<int> SaveChangesAsync();

    void Add(T entity);
    ValueTask<EntityEntry<T>> AddAsync(T entity);
    void Add<TEntity>(TEntity entity) where TEntity : class;
    void AddRange(IEnumerable<T> entities);
    void AddRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;
    ValueTask<EntityEntry<TEntity>> AddAsync<TEntity>(TEntity entity) where TEntity : class;
    Task AddRangeAsync<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;

    void Update(T entity);
    void Update<TEntity>(TEntity entity) where TEntity : class;
    void UpdateRange(IEnumerable<T> entities);
    void UpdateRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;

    void Remove(T entity);
    void Remove<TEntity>(TEntity entity) where TEntity : class;
    void RemoveRange(IEnumerable<T> entities);
    void RemoveRange<TEntity>(IEnumerable<TEntity> entities) where TEntity : class;
    void ClearTracking();

    IRepository<T> NewScope();

    Task<ITransaction> StartTransactionAsync();
}