using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using WebAPI.DAL.Entities;

namespace WebAPI.DAL;

public class AppDbContext : NhIdentityDbContext<
    NhDivision,
    DivisionUser,
    DivisionRole,
    DivisionUserRole,
    DivisionRoleClaim,
    User,
    UserRole,
    Log,
    LogMessageArgument,
    LogFile,
    LogMessageTranslated
    >
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