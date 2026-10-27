using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common.Authentication;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models.Options;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// Which Microsoft tenants may sign in and link accounts by mail, and the provider key of a link.
/// </summary>
public sealed class NhMicrosoftOAuthIdentityTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-1111-4222-8333-444444444444");
    private static readonly Guid OtherTenant = Guid.Parse("bbbbbbbb-5555-4666-8777-888888888888");

    [Fact]
    public void AnAllowListAdmitsOnlyItsTenantsAndTrustsThemToLinkByMail()
    {
        var settings = new MicrosoftAuthSettings { TenantId = "common", AllowedTenantIds = [Tenant.ToString().ToUpperInvariant()] };

        Assert.True(Identity(Tenant).TenantMaySignIn(settings));
        Assert.True(Identity(Tenant).MayLinkByEmail(settings));
        Assert.False(Identity(OtherTenant).TenantMaySignIn(settings));
        Assert.False(Identity(OtherTenant).MayLinkByEmail(settings));
    }

    [Fact]
    public void ASingleTenantAuthorityAdmitsOnlyItsTenantAndTrustsItToLinkByMail()
    {
        var settings = new MicrosoftAuthSettings { TenantId = Tenant.ToString() };

        Assert.True(Identity(Tenant).TenantMaySignIn(settings));
        Assert.True(Identity(Tenant).MayLinkByEmail(settings));
        Assert.False(Identity(OtherTenant).TenantMaySignIn(settings));
    }

    [Theory]
    [InlineData("common")]
    [InlineData("organizations")]
    [InlineData("contoso.example.test")]
    public void WithoutNamedTenantsEveryTenantSignsInButNoneLinksByMail(string authority)
    {
        var settings = new MicrosoftAuthSettings { TenantId = authority };

        Assert.True(Identity(OtherTenant).TenantMaySignIn(settings));
        Assert.False(Identity(OtherTenant).MayLinkByEmail(settings));
    }

    [Fact]
    public void LinkingByMailCanBeTurnedOff()
    {
        var settings = new MicrosoftAuthSettings { AllowedTenantIds = [Tenant.ToString()], LinkAccountsByEmail = false };

        Assert.True(Identity(Tenant).TenantMaySignIn(settings));
        Assert.False(Identity(Tenant).MayLinkByEmail(settings));
    }

    [Fact]
    public void TheProviderKeyIsTheTenantAndObjectIdAsLowercaseGuids()
    {
        var objectId = Guid.NewGuid();

        var login = NhMicrosoftOAuthLogins.LoginInfo(Tenant.ToString().ToUpperInvariant(), objectId.ToString("B"));

        Assert.Equal("microsoft-oauth", login.LoginProvider);
        Assert.Equal($"{Tenant:D}/{objectId:D}", login.ProviderKey);
        Assert.Throws<ArgumentException>(() => NhMicrosoftOAuthLogins.ProviderKey("contoso.example.test", objectId.ToString()));
    }

    [Fact]
    public void AnAllowedTenantThatIsNotATenantIdStopsTheRegistration()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://api.example.test",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "microsoft-oauth-identity-tests-signing-key",
        });
        var platform = builder.Services.AddNewHeapPlatformAspNetCommon<
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
            RegistrationTestDbContext,
            NhUserManager,
            NhDivisionService,
            NhDivisionMutateModel,
            NhDivisionUserService,
            NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            platform.AddAuthentication<NhUserViewModel<NhDivisionViewModel>, NhDivisionViewModel, NhClaimViewModel>(authentication =>
                authentication.AddMicrosoftOAuth(oauth => oauth.Settings.AllowedTenantIds = [Tenant.ToString(), "contoso.example.test"])));

        Assert.Contains("'contoso.example.test'", exception.Message, StringComparison.Ordinal);
    }

    private static NhMicrosoftOAuthIdentity Identity(Guid tenantId)
    {
        return new NhMicrosoftOAuthIdentity(tenantId, Guid.NewGuid());
    }

    private sealed class RegistrationTestDbContext(DbContextOptions<RegistrationTestDbContext> options)
        : NhIdentityDbContext(options);
}
