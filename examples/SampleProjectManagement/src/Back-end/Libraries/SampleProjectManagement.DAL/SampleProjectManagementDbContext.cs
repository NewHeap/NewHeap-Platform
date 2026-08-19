using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using SampleProjectManagement.DAL.Entities;

namespace SampleProjectManagement.DAL;

public class SampleProjectManagementDbContext : NhIdentityDbContext<
    NhDivision,
    NhDivisionUser,
    NhDivisionRole,
    NhDivisionUserRole,
    NhDivisionRoleClaim,
    NhUser,
    NhUserRole,
    NhLog,
    NhLogMessageArgument,
    NhLogFile,
    NhLogMessageTranslated>
{
    public SampleProjectManagementDbContext()
    {
    }

    public SampleProjectManagementDbContext(DbContextOptions<SampleProjectManagementDbContext> contextOptions)
        : base(contextOptions)
    {
    }

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Project>(entity =>
        {
            entity.HasIndex(x => new { x.DivisionId, x.Key }).IsUnique();

            entity
                .HasOne(x => x.Division)
                .WithMany()
                .HasForeignKey(x => x.DivisionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity
                .HasOne(x => x.OwnerUser)
                .WithMany()
                .HasForeignKey(x => x.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ProjectTask>(entity =>
        {
            entity.HasIndex(x => new { x.ProjectId, x.Title });

            entity
                .HasOne(x => x.Project)
                .WithMany(x => x.Tasks)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
