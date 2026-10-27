using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.AspNet.Common.Services.Notification;
using SampleProjectManagement.Api.Models;
using SampleProjectManagement.Api.Services;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// Evidence that the sample's branded code e-mail keeps the code out of the notification store:
/// its delivery expires with the code, so the notification processor removes it once it is sent.
/// </summary>
public sealed class TwoFactorMessageComposerSamplesTests
{
    [Fact]
    public async Task TheBrandedCodeEmailExpiresWithTheCode()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        using var services = new ServiceCollection().BuildServiceProvider();
        var composer = new SampleTwoFactorMessageComposer(
            new EnglishTextLocalizer(),
            TimeProvider.System,
            new RazorViewService(new CodeOnlyViewEngine(), new NoTempDataProvider(), services));

        var notification = await composer.ComposeEmailCodeAsync(
            new NhTwoFactorEmailCodeMessage
            {
                UserId = Guid.NewGuid(),
                Email = "person@example.test",
                Code = "482913",
                ExpiresAt = expiresAt,
                Purpose = NhTwoFactorEmailCodePurpose.SignIn,
            },
            TestContext.Current.CancellationToken);

        var delivery = Assert.Single(notification.Deliveries);
        Assert.Equal(NhEmailNotificationDispatcher.DispatcherIdValue, delivery.DispatcherId);
        var email = Assert.IsType<NhEmailDeliveryData>(delivery.Data);
        Assert.Equal(["person@example.test"], email.To);
        Assert.Contains("482913", email.Body, StringComparison.Ordinal);
        Assert.True(email.IsBodyHtml);
        Assert.Equal(expiresAt, email.ExpiresAt);
    }

    private sealed class EnglishTextLocalizer : IStringLocalizer<NhTwoFactorMessages>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] =>
            new(name, string.Format(CultureInfo.InvariantCulture, name, arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
        {
            return [];
        }
    }

    /// <summary>Stands in for the Razor mail layout and writes only the code.</summary>
    private sealed class CodeOnlyViewEngine : IRazorViewEngine
    {
        public ViewEngineResult FindView(ActionContext context, string viewName, bool isMainPage)
        {
            return ViewEngineResult.Found(viewName, new CodeOnlyView(viewName));
        }

        public ViewEngineResult GetView(string? executingFilePath, string viewPath, bool isMainPage)
        {
            return ViewEngineResult.Found(viewPath, new CodeOnlyView(viewPath));
        }

        public RazorPageResult FindPage(ActionContext context, string pageName)
        {
            throw new NotSupportedException();
        }

        public RazorPageResult GetPage(string executingFilePath, string pagePath)
        {
            throw new NotSupportedException();
        }

        public string? GetAbsolutePath(string? executingFilePath, string? pagePath)
        {
            return pagePath;
        }
    }

    private sealed class CodeOnlyView(string path) : IView
    {
        public string Path { get; } = path;

        public Task RenderAsync(ViewContext context)
        {
            var model = (TwoFactorCodeMailViewModel)context.ViewData.Model!;
            return context.Writer.WriteAsync($"<p>{model.Code}</p>");
        }
    }

    private sealed class NoTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context)
        {
            return new Dictionary<string, object>();
        }

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
