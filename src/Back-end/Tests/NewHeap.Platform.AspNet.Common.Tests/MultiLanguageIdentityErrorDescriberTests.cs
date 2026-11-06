using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Identity.Describers;
using NewHeap.Platform.AspNet.Common.Models.Mutate;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.AspNet.Common.PostgreSql;
using NewHeap.Platform.AspNet.Common.Services;
using System.Globalization;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

/// <summary>
/// The platform's Identity error describer ships English and Dutch resources, resolved through the
/// composite localizer that <c>AddNewHeapPlatformAspNetCommon</c> registers: Dutch for nl-NL and
/// English for every culture without resources of its own.
/// </summary>
public sealed class MultiLanguageIdentityErrorDescriberTests
{
    private static readonly IReadOnlyList<(string Name, Func<IdentityErrorDescriber, IdentityError> Describe)> Messages =
    [
        (nameof(IdentityErrorDescriber.DefaultError), describer => describer.DefaultError()),
        (nameof(IdentityErrorDescriber.ConcurrencyFailure), describer => describer.ConcurrencyFailure()),
        (nameof(IdentityErrorDescriber.PasswordMismatch), describer => describer.PasswordMismatch()),
        (nameof(IdentityErrorDescriber.InvalidToken), describer => describer.InvalidToken()),
        (nameof(IdentityErrorDescriber.RecoveryCodeRedemptionFailed), describer => describer.RecoveryCodeRedemptionFailed()),
        (nameof(IdentityErrorDescriber.LoginAlreadyAssociated), describer => describer.LoginAlreadyAssociated()),
        (nameof(IdentityErrorDescriber.InvalidUserName), describer => describer.InvalidUserName("a b")),
        (nameof(IdentityErrorDescriber.InvalidEmail), describer => describer.InvalidEmail("not-an-address")),
        (nameof(IdentityErrorDescriber.DuplicateUserName), describer => describer.DuplicateUserName("taken")),
        (nameof(IdentityErrorDescriber.DuplicateEmail), describer => describer.DuplicateEmail("taken@example.test")),
        (nameof(IdentityErrorDescriber.InvalidRoleName), describer => describer.InvalidRoleName("")),
        (nameof(IdentityErrorDescriber.DuplicateRoleName), describer => describer.DuplicateRoleName("Manager")),
        (nameof(IdentityErrorDescriber.UserAlreadyHasPassword), describer => describer.UserAlreadyHasPassword()),
        (nameof(IdentityErrorDescriber.UserLockoutNotEnabled), describer => describer.UserLockoutNotEnabled()),
        (nameof(IdentityErrorDescriber.UserAlreadyInRole), describer => describer.UserAlreadyInRole("Manager")),
        (nameof(IdentityErrorDescriber.UserNotInRole), describer => describer.UserNotInRole("Manager")),
        (nameof(IdentityErrorDescriber.PasswordTooShort), describer => describer.PasswordTooShort(8)),
        (nameof(IdentityErrorDescriber.PasswordRequiresUniqueChars), describer => describer.PasswordRequiresUniqueChars(3)),
        (nameof(IdentityErrorDescriber.PasswordRequiresNonAlphanumeric), describer => describer.PasswordRequiresNonAlphanumeric()),
        (nameof(IdentityErrorDescriber.PasswordRequiresDigit), describer => describer.PasswordRequiresDigit()),
        (nameof(IdentityErrorDescriber.PasswordRequiresLower), describer => describer.PasswordRequiresLower()),
        (nameof(IdentityErrorDescriber.PasswordRequiresUpper), describer => describer.PasswordRequiresUpper()),
    ];

    [Fact]
    public void EveryMessageIsDutchForNlNlAndEnglishForACultureWithoutResources()
    {
        using var application = BuildApplication();
        using var scope = application.Services.CreateScope();
        var describer = scope.ServiceProvider.GetRequiredService<IdentityErrorDescriber>();
        Assert.IsType<MultiLanguageIdentityErrorDescriber>(describer);

        var english = Describe(describer, "fr-FR");
        var dutch = Describe(describer, "nl-NL");

        Assert.Equal("Passwords must be at least 8 characters.", english[nameof(IdentityErrorDescriber.PasswordTooShort)]);
        Assert.Equal("Email 'taken@example.test' is already taken.", english[nameof(IdentityErrorDescriber.DuplicateEmail)]);
        Assert.Equal("Wachtwoorden moeten minstens 8 tekens lang zijn.", dutch[nameof(IdentityErrorDescriber.PasswordTooShort)]);
        Assert.Equal("E-mailadres 'taken@example.test' is al in gebruik.", dutch[nameof(IdentityErrorDescriber.DuplicateEmail)]);
        Assert.Equal("Onjuist wachtwoord.", dutch[nameof(IdentityErrorDescriber.PasswordMismatch)]);

        // Every message has a Dutch translation, and English stays the language of other cultures.
        Assert.All(Messages, message => Assert.NotEqual(english[message.Name], dutch[message.Name]));
        Assert.Equal(english, Describe(describer, "en-US"));
    }

    [Fact]
    public void TheErrorCodesDoNotDependOnTheCulture()
    {
        using var application = BuildApplication();
        using var scope = application.Services.CreateScope();
        var describer = scope.ServiceProvider.GetRequiredService<IdentityErrorDescriber>();

        foreach (var culture in new[] { "nl-NL", "fr-FR" })
        {
            WithUiCulture(culture, () =>
            {
                Assert.All(Messages, message => Assert.Equal(message.Name, message.Describe(describer).Code));
            });
        }
    }

    private static Dictionary<string, string> Describe(IdentityErrorDescriber describer, string culture)
    {
        var descriptions = new Dictionary<string, string>();
        WithUiCulture(culture, () =>
        {
            foreach (var message in Messages)
            {
                descriptions[message.Name] = message.Describe(describer).Description;
            }
        });

        return descriptions;
    }

    private static void WithUiCulture(string culture, Action action)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    /// <summary>
    /// The platform registration with Identity, as an application composes it; nothing here opens
    /// the database.
    /// </summary>
    private static WebApplication BuildApplication()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Issuer"] = "https://describer.example.test",
            ["NewHeap:PlatformAspNetCommon:Authorization:JWT:Token:Key"] = "identity-error-describer-tests-key",
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
                DescriberTestDbContext,
                NhUserManager,
                NhDivisionService,
                NhDivisionMutateModel,
                NhDivisionUserService,
                NhDivisionUserMutateModel>(NewHeapAspNetCommonOptions.Builder(builder.Configuration).Build())
            .WithIdentityEntityFramework(options => options.UseNewHeapPostgreSql("Host=localhost;Database=unused"))
            .WithIdentity()
            .WithDbLogService(_ => { });

        return builder.Build();
    }

    private sealed class DescriberTestDbContext(DbContextOptions<DescriberTestDbContext> options)
        : NhIdentityDbContext(options);
}
