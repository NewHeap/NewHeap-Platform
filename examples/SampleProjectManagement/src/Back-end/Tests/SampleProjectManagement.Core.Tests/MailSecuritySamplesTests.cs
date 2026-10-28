using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common;
using NewHeap.Platform.Common.Models.Options;
using SampleProjectManagement.Api.Composition;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-355: the API's mail settings name their SMTP security mode. Its <c>appsettings.json</c> sends in
/// plain text to the development mail server on <c>localhost:1025</c>, a deployment requires STARTTLS
/// through its own settings, and a mode that does not exist stops the host before any mail is sent.
/// </summary>
public sealed class MailSecuritySamplesTests
{
    [Fact]
    public async Task TheApiSendsInPlainTextToItsDevelopmentMailServer()
    {
        using var host = BuildHost();

        await host.StartAsync(TestContext.Current.CancellationToken);

        var settings = host.Services.GetRequiredService<IOptions<MailServiceSettings>>().Value;
        Assert.Equal(NhSmtpSecurityMode.None, settings.SecurityMode);
        Assert.Equal(NhSmtpSecurityMode.None, settings.ResolveSecurityMode());
        Assert.Equal("localhost", settings.Host);
        Assert.Equal(1025, settings.Port);
        // Without a user the mail service does not authenticate.
        Assert.Equal("", settings.User);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ADeploymentRequiresStartTlsWithItsOwnSettings()
    {
        using var host = BuildHost(
            $"--{SampleMailComposition.SectionName}:SecurityMode=StartTlsRequired",
            $"--{SampleMailComposition.SectionName}:Host=smtp.sample.test",
            $"--{SampleMailComposition.SectionName}:Port=587",
            $"--{SampleMailComposition.SectionName}:User=sample-mailer");

        await host.StartAsync(TestContext.Current.CancellationToken);

        var settings = host.Services.GetRequiredService<IOptions<MailServiceSettings>>().Value;
        Assert.Equal(NhSmtpSecurityMode.StartTlsRequired, settings.ResolveSecurityMode());
        Assert.Equal(587, settings.Port);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AModeThatDoesNotExistStopsTheHost()
    {
        // NewHeap has no "when available" mode: STARTTLS is required or not used.
        using var host = BuildHost($"--{SampleMailComposition.SectionName}:SecurityMode=StartTlsWhenAvailable");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("SecurityMode", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host with the API's own <c>appsettings.json</c> and its mail registration.
    /// </summary>
    private static IHost BuildHost(params string[] args)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false);
        builder.Configuration.AddCommandLine(args);
        builder.Services
            .AddNewHeapPlatformCommon(NewHeapCommonOptions.Builder(builder.Configuration).Build())
            .WithSampleMail(builder.Configuration);

        return builder.Build();
    }
}
