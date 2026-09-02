using Microsoft.Extensions.Configuration;

namespace NewHeap.Platform.Common.Models.Options;

public class NewHeapCommonSettings
{
}


public class NewHeapCommonOptions
{
    public const string DefaultSettingsPrefix = "NewHeap:PlatformCommon";

    public static NewHeapCommonOptionsBuilder Builder(IConfiguration configuration)
    => new(configuration);

    public required Action<NewHeapCommonSettings> SettingsAction { get; set; }

    public bool OtlpUseExporter = false;
}

public class NewHeapCommonOptionsBuilder
{
    private readonly IConfiguration _configuration;
    private NewHeapCommonOptions? _options;

    public NewHeapCommonOptionsBuilder(IConfiguration configuration)
    {
        _configuration = configuration;
        _options = new NewHeapCommonOptions
        {
            SettingsAction =
                x => _configuration.GetSection($"{NewHeapCommonOptions.DefaultSettingsPrefix}:Settings").Bind(x)
        };
    }

    public NewHeapCommonOptionsBuilder ConfigureSettings(Action<NewHeapCommonSettings> settingsAction)
    {
        ThrowIfBuild();
        _options!.SettingsAction = settingsAction;
        return this;
    }

    public NewHeapCommonOptionsBuilder UseOtlpExporter(bool use = true)
    {
        ThrowIfBuild();
        _options!.OtlpUseExporter = use;
        return this;
    }

    [Obsolete($"Use {nameof(UseOtlpExporter)} instead. Standard OTEL_* environment variables are preferred.")]
    public NewHeapCommonOptionsBuilder UseOtlpUseExporter(bool use = true) => UseOtlpExporter(use);

    public NewHeapCommonOptions Build()
    {
        var options = _options;
        _options = null;
        return options!;
    }

    private void ThrowIfBuild()
    {
        if (_options == null)
        {
            throw new InvalidOperationException("The options have already been built.");
        }
    }
}
