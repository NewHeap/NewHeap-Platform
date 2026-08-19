using Microsoft.EntityFrameworkCore;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;

namespace SampleProjectManagement.DAL;

internal class SampleProjectManagementDbContextFactory : NhDbContextFactory<
    SampleProjectManagementDbContext,
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
    public override SampleProjectManagementDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SAMPLE_PROJECT_MANAGEMENT_POSTGRESQL_CONNECTION")
            ?? "Host=localhost;Database=sample-project-management;Username=postgres;Password=postgres";
        var options = CreateBuilder();

        options.UseNpgsql(
            connectionString,
            npgsqlOptions => npgsqlOptions.CommandTimeout((int)TimeSpan.FromMinutes(2).TotalSeconds));

        return new SampleProjectManagementDbContext(options.Options);
    }
}
