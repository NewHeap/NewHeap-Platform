using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NewHeap.Platform.Common.Localization;
using NewHeap.Platform.Common.Translations;
using System.Globalization;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-092: German resources must be present in the Resources path that the
/// NewHeap composite localizer configures, not only in a legacy folder.
/// </summary>
public sealed class NewHeapLocalizationSamplesTests
{
    [Fact]
    public void GermanSharedAndDataAnnotationResourcesResolveFromConfiguredResourcePath()
    {
        var previousCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var factory = CreateFactory();

            Assert.Equal("Rechnung {0}", factory.Create(typeof(SharedResources))["Invoice {0}"].Value);
            Assert.Equal(
                "Dem Benutzer zugewiesen",
                factory.Create(typeof(SharedDataAnnotationRecources))["AssignedToUser"].Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    private static NhResourceManagerStringLocalizerFactory CreateFactory()
    {
        var options = Options.Create(new NhLocalizationOptions
        {
            AssemblyNameLocalizationOptions =
            [
                new NhLocalizationOptions.Entry
                {
                    AssemblyName = typeof(SharedResources).Assembly.GetName(),
                    Options = new LocalizationOptions { ResourcesPath = "Resources" },
                    Order = 0
                }
            ]
        });

        return new NhResourceManagerStringLocalizerFactory(options, NullLoggerFactory.Instance);
    }
}
