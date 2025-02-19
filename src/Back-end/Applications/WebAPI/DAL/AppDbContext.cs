using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using WebAPI.DAL.Entities;

namespace WebAPI.DAL;

public class AppDbContext : NhDbContext
{
    public AppDbContext()
    {
    }

    public AppDbContext(DbContextOptions contextOptions)
        : base(contextOptions)
    {
    }

    public DbSet<Address> Addresses { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
    }
}