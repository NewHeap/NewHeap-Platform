using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using Xunit;

namespace NewHeap.Platform.Common.Test;
public class NhTestingContext : IDisposable, IAsyncDisposable
{
    protected IServiceCollection Services { get; } = new ServiceCollection();

    protected ServiceProvider ServiceProvider { get; private set; } = null!;

    protected virtual void ConfigureServices(IServiceCollection services) { }

    protected virtual void ConfigureTestServices(IServiceCollection services) { }

    public IServiceScope CreateScope() => ServiceProvider.CreateScope();

    public T GetRequiredService<T>()
        where T : notnull
        => ServiceProvider.GetRequiredService<T>();

    public virtual async Task BuildAsync()
    {
        ConfigureServices(Services);
        ConfigureTestServices(Services);

        ServiceProvider = Services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

        await Task.CompletedTask;
    }

    public virtual void Dispose()
    {
        if (ServiceProvider is IDisposable d)
        {
            d.Dispose();
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (ServiceProvider is IAsyncDisposable d)
        {
            await d.DisposeAsync();
        }
    }
}

public class NhTestingContextFixture<TContext>
    : IAsyncLifetime
    where TContext : NhTestingContext, new()
{
    public TContext Context { get; private set; } = null!;

    public virtual async Task InitializeAsync()
    {
        Context = new TContext();

        await Context.BuildAsync();
    }

    public virtual async Task DisposeAsync()
    {
        await Context.DisposeAsync();
    }
}

public class NhDbContextTestingContextFixture<TContext> : NhTestingContextFixture<TContext>
    where TContext : NhTestingContext, new()
{
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
