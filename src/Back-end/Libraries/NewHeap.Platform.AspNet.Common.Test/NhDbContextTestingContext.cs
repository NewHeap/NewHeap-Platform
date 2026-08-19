using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.Common.Test;
using System.Reflection;

namespace NewHeap.Platform.AspNet.Common.Test;
public class NhDbContextTestingContext<TDbContext> : NhTestingContext
    where TDbContext : DbContext
{
    public TDbContext DbContext { get; set; } = null!;

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<TDbContext>((services) => {
            return NhInMemoryDbContextFactory.Create<TDbContext>();
        });

        AutoConfigureRepositories(services);
    }

    protected virtual IServiceCollection AutoConfigureRepositories(IServiceCollection services)
    {
        // Scan the DbContext its assamblies for entities and register repositories
        var entityTypes = typeof(TDbContext)
         .GetProperties(BindingFlags.Public | BindingFlags.Instance)
         .Where(p =>
             p.PropertyType.IsGenericType &&
             p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
         .Select(p => p.PropertyType.GetGenericArguments()[0])
         .Distinct();

        var method = GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m =>
                m.Name == nameof(ConfigureRepository) &&
                m.IsGenericMethodDefinition &&
                m.GetGenericArguments().Length == 1 &&
                m.GetParameters().Length == 1);

        foreach (var entityType in entityTypes)
        {
            var generic = method.MakeGenericMethod(entityType);
            generic.Invoke(this, new object[] { services });
        }

        return services;
    }

    protected virtual IServiceCollection ConfigureRepository<TEntity>(IServiceCollection services)
        where TEntity : class
    {
        services.AddScoped<IRepository<TEntity>>((facServices) => {
            var dbContext = facServices.GetRequiredService<TDbContext>();
            return new Repository<TEntity>(dbContext, facServices);
        });

        return services;
    }

    protected virtual IServiceCollection ConfigureRepository<TEntity, TRepository>(
        IServiceCollection services, 
        Func<TDbContext, TRepository> factory)
        where TEntity : class
        where TRepository : Repository<TEntity>
    {
        services.AddScoped<TRepository>((facServices) => {
            var dbContext = facServices.GetRequiredService<TDbContext>();
            return factory(dbContext);
        });

        return services;
    }
}

public static class NhInMemoryDbContextFactory
{
    public static DbContextOptions<TDbContext> CreateOptions<TDbContext>()
        where TDbContext : DbContext
    {
        return new DbContextOptionsBuilder<TDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
    }

    public static TDbContext Create<TDbContext>()
        where TDbContext : DbContext
    {
        var options = CreateOptions<TDbContext>();

        // Use reflection to create instance with options parameter
        return (TDbContext)Activator.CreateInstance(typeof(TDbContext), options)!;
    }
}
