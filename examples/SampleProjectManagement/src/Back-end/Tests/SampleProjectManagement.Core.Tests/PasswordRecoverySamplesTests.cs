using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using SampleProjectManagement.Api.Authorization;
using SampleProjectManagement.Api.Composition;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.DAL;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-075: the recovery mail links to the reset page on the application origin that the API's
/// configuration names, whatever the anonymous request supplies as its reset URL, host or origin,
/// and the token in that link resets the password.
/// </summary>
public sealed partial class PasswordRecoverySamplesTests(SamplePostgreSqlServer postgreSql)
{
    private const string NewPassword = "Recovered3!Sample";

    [Fact]
    public async Task TheRecoveryLinkPointsAtTheConfiguredApplicationOrigin()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var mail = new RecordingMailService();
        using var application = BuildApplication(await postgreSql.CreateMigratedDatabaseAsync("password_recovery"), mail);
        await using var scope = application.Services.CreateAsyncScope();
        await SampleDevelopmentIdentitySeeder.SeedAsync(scope.ServiceProvider);

        // The anonymous request names another site as its reset URL, host and origin.
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = new DefaultHttpContext
        {
            Request =
            {
                Scheme = "https",
                Host = new HostString("elsewhere.example.test"),
                Headers = { Origin = "https://elsewhere.example.test" },
            },
        };
        var recovery = scope.ServiceProvider.GetRequiredService<AccountSampleService>();
#pragma warning disable CS0618 // The request still carries the obsolete reset URL; the service ignores it.
        await recovery.RecoverPasswordAsync(
            new NhRecoverPasswordUserMutateModel
            {
                Email = SampleAuthorizationDefaults.ViewerEmail,
                ResetUrl = "https://elsewhere.example.test/reset",
            },
            cancellationToken);
#pragma warning restore CS0618

        var message = Assert.Single(mail.Messages);
        Assert.Equal([SampleAuthorizationDefaults.ViewerEmail], message.To);
        Assert.DoesNotContain("elsewhere.example.test", message.Body, StringComparison.OrdinalIgnoreCase);
        var link = new Uri(LinkPattern().Match(message.Body).Value);
        Assert.Equal("http://localhost:4210/reset-password", link.GetLeftPart(UriPartial.Path));

        // The link's token resets the viewer's password.
        var query = QueryHelpers.ParseQuery(link.Query);
        var reset = await recovery.ResetPasswordAsync(
            new NhResetPasswordUserMutateModel
            {
                UserId = Guid.Parse(query["userId"]!),
                Token = query["token"],
                Password = NewPassword,
                ConfirmPassword = NewPassword,
            },
            cancellationToken);
        Assert.True(reset.Success);

        // An address without an account receives nothing.
        await recovery.RecoverPasswordAsync(
            new NhRecoverPasswordUserMutateModel { Email = "nobody@example.test" },
            cancellationToken);
        Assert.Single(mail.Messages);
    }

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex LinkPattern();

    private static WebApplication BuildApplication(string connectionString, RecordingMailService mail)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        // The API's own appsettings.json, which names the management app as the application origin.
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "sample-project-management-recovery-signing-key",
        });

        builder.Services
            .AddNewHeapPlatformAspNetCommon<
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
                NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build())
            .AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(options =>
            {
                options.WithAuthenticationService<SampleAuthenticationService>();
                options.AddUserNamePasswordAuthentication(authentication => authentication.EnableRefreshToken = true);
            })
            .WithIdentityEntityFramework(options => options.UseNewHeapPostgreSql(connectionString))
            .WithIdentity()
            .WithDbLogService(_ => { });

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<NhMailService>(mail);
        // As Program.cs registers the account service and its recovery settings.
        builder.Services.AddSampleAccountRecovery(builder.Configuration);
        builder.Services.AddScoped<AccountSampleService>();

        return builder.Build();
    }

    private sealed record SentMail(IReadOnlyList<string> To, string Subject, string Body);

    /// <summary>Records the mail the sample would send, instead of sending it.</summary>
    private sealed class RecordingMailService()
        : NhMailService(Options.Create(new MailServiceSettings()), Microsoft.Extensions.Logging.Abstractions.NullLogger<NhMailService>.Instance)
    {
        public List<SentMail> Messages { get; } = [];

        public override Task SendAsync(
            MailMessage mailMessage,
            MailAddress? fromMailAddress = null,
            string? formDisplayName = null,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(new SentMail(mailMessage.To.Select(address => address.Address).ToList(), mailMessage.Subject, mailMessage.Body));
            return Task.CompletedTask;
        }
    }
}
