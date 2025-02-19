using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Linq.Expressions;
using System.Reflection;

namespace NewHeap.Platform.AspNet.Common.DAL;

public partial class Repository<T> : IRepository<T> where T : class
{
    protected DbSet<T> DbSet;

    public Repository(NhDbContext context)
    {
        Context = context;
        DbSet = context.Set<T>();
    }

    public NhDbContext Context { get; }

    public DbSet<TDbSet> GetDbSet<TDbSet>()
        where TDbSet : class
    {
        return Context.Set<TDbSet>();
    }

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

    public virtual ValueTask<EntityEntry<TEntity>> AddAsync<TEntity>(TEntity entity)
        where TEntity : class
    {
        return Context.Set<TEntity>().AddAsync(entity);
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

    public virtual Task AddRangeAsync<TEntity>(IEnumerable<TEntity> entities)
        where TEntity : class
    {
        return Context.Set<TEntity>().AddRangeAsync(entities);
    }

    public virtual T? Find<TKey>(TKey id)
    {
        return DbSet.Find(id);
    }

    public virtual ValueTask<T?> FindAsync<TKey>(TKey id)
    {
        return DbSet.FindAsync(id);
    }

    public virtual T? FindOneBy(Expression<Func<T, bool>> predicate)
    {
        return DbSet.FirstOrDefault(predicate);
    }

    public virtual Task<T?> FindOneByAsync(Expression<Func<T, bool>> predicate)
    {
        return DbSet.FirstOrDefaultAsync(predicate);
    }

    public virtual IQueryable<T> FindBy(Expression<Func<T, bool>> predicate)
    {
        return DbSet.Where(predicate);
    }

    public virtual bool Any(Expression<Func<T, bool>> predicate)
    {
        return DbSet.Any(predicate);
    }

    public virtual Task<bool> AnyAsync(Expression<Func<T, bool>> predicate)
    {
        return DbSet.AnyAsync(predicate);
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
        var opt = typeof(NhDbContext).GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Context);
        var context = (NhDbContext)Activator.CreateInstance(type, opt)!;
        return new Repository<T>(context);
    }

    public async Task<ITransaction> StartTransactionAsync()
    {
        var trans = await Context.Database.BeginTransactionAsync();
        return new Transaction(trans);
    }

    public virtual int SaveChanges()
    {
        return Context.SaveChanges();
    }

    public virtual Task<int> SaveChangesAsync()
    {
        return Context.SaveChangesAsync();
    }

    public virtual Task AddRangeAsync(IEnumerable<T> entities)
    {
        return DbSet.AddRangeAsync(entities);
    }
}