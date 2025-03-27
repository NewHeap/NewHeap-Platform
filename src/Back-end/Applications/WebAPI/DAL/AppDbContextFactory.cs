using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using System;

namespace WebAPI.DAL;

internal class AppDbContextFactory : NhDbContextFactory<
    AppDbContext,
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
    NhLogMessageTranslated
    >
{
    public override AppDbContext CreateDbContext(string[] args)
    {
        var configuration = CreateConfigurationRoot();
        var builder = CreateBuilder();
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        builder.UseSqlServer(connectionString, opts => opts.CommandTimeout((int)TimeSpan.FromMinutes(10).TotalSeconds));

        return new AppDbContext(builder.Options);
    }
}