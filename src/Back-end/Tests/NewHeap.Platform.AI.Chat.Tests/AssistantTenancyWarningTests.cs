using Microsoft.Extensions.Logging;
using NewHeap.Platform.AI.Chat.Runs;
using NewHeap.Platform.AI.Chat.Tests.Infrastructure;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The tenant rule warns operators about a caller without a tenant who addresses work stored under a
/// tenant. That caller reads the work as unknown, as a caller of another tenant does, but it nearly
/// always means the application passed no tenant for the person. The warning names the path and the
/// subject type only and repeats at most once per interval for each path. A caller of another tenant
/// and a listing warn about nothing.
/// </summary>
public sealed class AssistantTenancyWarningTests
{
    private const string Tenant = "tenant-a";
    private const string SubjectId = "subject-42";

    private static readonly NhAiSubjectInfo Project = new(
        new NhAiSubjectRef("project", SubjectId),
        NhAiSubjectInfo.CreateScopeKey(Tenant, null))
    {
        TenantId = Tenant
    };

    [Fact]
    public void A_caller_without_a_tenant_refused_a_tenants_work_is_warned_about_once_per_path()
    {
        var logs = new CapturingLoggerProvider();
        var tenancy = Create(logs);

        Assert.False(tenancy.Reaches(Project, null));
        Assert.False(tenancy.Reaches(Project, null));
        var warning = Assert.Single(Warnings(logs));
        Assert.StartsWith(
            nameof(AssistantTenancyWarningTests) + "." + nameof(A_caller_without_a_tenant_refused_a_tenants_work_is_warned_about_once_per_path)
            + " refused agent work about a subject of type project",
            warning,
            StringComparison.Ordinal);
        Assert.Contains("NhAssistantDecidingPerson.TenantId", warning, StringComparison.Ordinal);
        // Neither the tenant nor the subject reaches the log.
        Assert.DoesNotContain(Tenant, warning, StringComparison.Ordinal);
        Assert.DoesNotContain(SubjectId, warning, StringComparison.Ordinal);

        // Another path warns on its own.
        Assert.False(RefuseElsewhere(tenancy));
        Assert.Equal(2, Warnings(logs).Length);
    }

    [Fact]
    public void A_caller_of_another_tenant_a_listing_and_work_the_caller_reaches_warn_about_nothing()
    {
        var logs = new CapturingLoggerProvider();
        var tenancy = Create(logs);
        var application = new NhAiSubjectInfo(NhAiSystemSubjects.Global, NhAiSubjectInfo.CreateScopeKey(null, null));

        // The isolation between tenants works as intended.
        Assert.False(tenancy.Reaches(Project, "tenant-b"));
        // A listing leaves out other tenants' work for every caller, also for an operator without one.
        Assert.False(tenancy.Includes(Project, null));
        // The caller's own tenant, and the whole application for a caller without a tenant.
        Assert.True(tenancy.Reaches(Project, Tenant));
        Assert.True(tenancy.Reaches(application, null));
        Assert.True(tenancy.Includes(application, null));

        Assert.Empty(Warnings(logs));
    }

    private static NhAssistantTenancy Create(CapturingLoggerProvider logs)
    {
        return new NhAssistantTenancy(
            new NhAssistantRegistrationState(),
            new LoggerFactory([logs]).CreateLogger<NhAssistantTenancy>());
    }

    private static bool RefuseElsewhere(NhAssistantTenancy tenancy)
    {
        return tenancy.Reaches(Project, null);
    }

    private static string[] Warnings(CapturingLoggerProvider logs)
    {
        return logs.Entries.Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message).ToArray();
    }
}
