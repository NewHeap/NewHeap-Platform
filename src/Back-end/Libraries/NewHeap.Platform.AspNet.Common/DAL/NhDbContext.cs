using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using Newtonsoft.Json;

namespace NewHeap.Platform.AspNet.Common.DAL;

public abstract partial class NhDbContext : IdentityDbContext<User, UserRole, Guid>
{
    public static readonly JsonSerializerSettings ConvertJsonSerializerSettings = new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
    public DbSet<Division> Divisions { get; set; }
    public DbSet<Log> Logs { get; set; }
    public DbSet<LogMessageArgument> LogMessageArguments { get; set; }
    public DbSet<LogFile> LogFiles { get; set; }
    public DbSet<LogMessageTranslated> LogMessageTranslateds { get; set; }
    public DbSet<DivisionRole> DivisionRoles { get; set; }
    public DbSet<DivisionUser> DivisionUsers { get; set; }
    public DbSet<DivisionUserRole> DivisionUserRoles { get; set; }
    public DbSet<DivisionRoleClaim> DivisionRoleClaims { get; set; }

    public NhDbContext()
        : base()
    {
    }

    public NhDbContext(DbContextOptions contextOptions)
        : base(contextOptions)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        #region User

        builder.Entity<User>()
            .HasOne(x => x.ActiveDivision)
            .WithMany()
            .HasForeignKey(x => x.ActiveDivisionId)
            .OnDelete(DeleteBehavior.SetNull)
            ;
        #endregion

        #region Log

        builder.Entity<Log>()
            .HasIndex(x => x.Tag)
            ;

        builder.Entity<Log>()
            .HasIndex(x => new { x.ObjectTypeFull, x.ObjectId })
            ;

        builder.Entity<Log>()
            .HasIndex(x => new { x.ObjectType, x.ObjectId })
            ;

        builder.Entity<Log>()
            .HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.SetNull)
            ;

        builder.Entity<Log>()
            .HasOne(x => x.Division)
            .WithMany()
            .HasForeignKey(x => x.DivisionId)
            .OnDelete(DeleteBehavior.SetNull)
            ;

        builder.Entity<LogFile>()
            .HasOne(x => x.Log)
            .WithMany(x => x.Files)
            .HasForeignKey(x => x.LogId)
            .OnDelete(DeleteBehavior.Cascade)
            ;

        builder.Entity<LogMessageArgument>()
            .HasOne(x => x.Log)
            .WithMany(x => x.MessageArguments)
            .HasForeignKey(x => x.LogId)
            .OnDelete(DeleteBehavior.Cascade)
            ;

        builder.Entity<LogMessageTranslated>()
            .HasOne(x => x.Log)
            .WithMany(x => x.MessageTranslateds)
            .HasForeignKey(x => x.LogId)
            .OnDelete(DeleteBehavior.Cascade)
            ;

        #endregion

        #region Division

        builder.Entity<Division>(entity =>
        {
            
        });

        builder.Entity<DivisionUser>(entity =>
        {
            entity.HasIndex(x => new { x.DivisionId, x.UserId })
                .IsUnique();

            entity
                .HasOne(x => x.Division)
                .WithMany(x => x.DivisionUsers)
                .HasForeignKey(x => x.DivisionId)
                .OnDelete(DeleteBehavior.Cascade)
                ;

            entity
                .HasOne(x => x.User)
                .WithMany(x => x.DivisionUsers)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                ;
        });

        builder.Entity<DivisionUserRole>(entity =>
        {
            entity.HasKey(x => new { x.DivisionUserId, x.DivisionRoleId });

            entity
                .HasOne(x => x.DivisionUser)
                .WithMany(x => x.DivisionUserRoles)
                .HasForeignKey(x => x.DivisionUserId)
                .OnDelete(DeleteBehavior.Cascade)
                ;

            entity
                .HasOne(x => x.DivisionRole)
                .WithMany(x => x.DivisionUserRoles)
                .HasForeignKey(x => x.DivisionRoleId)
                .OnDelete(DeleteBehavior.Cascade)
                ;
        });

        builder.Entity<DivisionRoleClaim>(entity =>
        {
            entity.HasIndex(x => x.DivisionRoleId);

            entity
                .HasOne(x => x.DivisionRole)
                .WithMany(x => x.DivisionRoleClaims)
                .HasForeignKey(x => x.DivisionRoleId)
                .OnDelete(DeleteBehavior.Cascade)
                ;
        });

        #endregion
    }
}