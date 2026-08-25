using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace NewHeap.Platform.Common;

public class NewHeapPlatformCommonConfigurator
{
    private readonly NewHeapCommonOptions _options;
    private readonly IServiceCollection _serviceCollection;

    public NewHeapPlatformCommonConfigurator(
        IServiceCollection serviceCollection,
        NewHeapCommonOptions options
    )
    {
        _serviceCollection = serviceCollection;
        _options = options;

        AddDefault();
    }

    private void AddDefault()
    {
        //Must register the options object as a singleton so it can be injected into the DbContext etc.
        _serviceCollection.AddSingleton(_options);
        _serviceCollection.Configure(_options.SettingsAction);

        _serviceCollection.AddSingleton<LogHelperService>();
        _serviceCollection.AddSingleton<ValidationService>();
        _serviceCollection.AddSingleton<ICollectionProcessingService, CollectionProcessingService>();

        if (_options.OtlpUseExporter)
        {
            if (!_serviceCollection.HasNewHeapOtlpExporterRegistration())
            {
                _serviceCollection.AddOpenTelemetry().UseOtlpExporter();
                _serviceCollection.MarkNewHeapOtlpExporterRegistered();
            }
        }

        _serviceCollection.AddServiceDiscovery();

        _serviceCollection.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            //http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });
    }

    public NewHeapPlatformCommonConfigurator WithMail(Action<MailServiceSettings> mailServiceSettingsAction)
    {
        _serviceCollection.Configure(mailServiceSettingsAction);
        _serviceCollection.AddScoped<NhMailService, NhMailService>();

        return this;
    }

}
