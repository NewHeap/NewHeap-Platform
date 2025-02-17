using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace NewHeap.Platform.AspNet.Common.DAL;

public partial class InternalNhDbContextFactory<TDbContext>
    where TDbContext : NhDbContext
{
    private readonly IConfiguration _config;

    public InternalNhDbContextFactory(IConfiguration config)
    {
        _config = config;
    }

    public TDbContext CreateDbContext(Action<SqlServerDbContextOptionsBuilder> sqlServerOptionsAction = null)
    {
        var optionsBuilder = new DbContextOptionsBuilder<NhDbContext>();
        optionsBuilder.UseSqlServer(_config.GetConnectionString("DefaultConnection"), sqlServerOptionsAction);

        return (TDbContext)Activator.CreateInstance(typeof(TDbContext), optionsBuilder.Options);
    }
}
