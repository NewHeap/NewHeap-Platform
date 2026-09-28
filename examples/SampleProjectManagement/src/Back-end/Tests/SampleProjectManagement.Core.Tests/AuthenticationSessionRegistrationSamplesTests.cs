using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Services;
using SampleProjectManagement.DAL;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public sealed class AuthenticationSessionRegistrationSamplesTests
{
    [Fact]
    public void PlatformAuthenticationServicesBuildWithStartupValidation()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateOnBuild = true;
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "sample-project-management",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] =
                "sample-project-management-signing-key-2026",
        });
        builder.Services.AddNewHeapPlatformAspNetCommon<
            NhUser,
            NhUserRole,
            NhDivision,
            NhDivisionUser,
            NhDivisionRole,
            NhDivisionUserRole,
            NhDivisionRoleClaim,
            NhLog,
            NhLogMessageArgument,
            NhLogFile,
            NhLogMessageTranslated,
            NhDbLogService,
            SampleProjectManagementDbContext,
            NhUserManager,
            NhDivisionService,
            NhDivisionMutateModel,
            NhDivisionUserService,
            NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build());

        using var application = builder.Build();

        Assert.NotNull(application.Services);
    }
}
