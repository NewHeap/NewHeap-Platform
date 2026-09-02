using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Reflection;

namespace NewHeap.Platform.Common.Localization;

public class NhLocalizationOptions
{
    public class Entry
    {
        public AssemblyName AssemblyName { get; set; } = null!;
        public LocalizationOptions Options { get; set; } = new();

        public int Order { get; set; }
    }

    public List<Entry> AssemblyNameLocalizationOptions { get; set; } = new();
}

public class NhResourceManagerStringLocalizerFactory : IStringLocalizerFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<NhLocalizationOptions.Entry> _entries;

    private readonly ConcurrentDictionary<AssemblyName, ResourceManagerStringLocalizerFactory> _factories = new();
    private readonly ConcurrentDictionary<(string baseName, string location), IStringLocalizer> _compositeCache = new();

    public NhResourceManagerStringLocalizerFactory(
        IOptions<NhLocalizationOptions> options,
        ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _entries = options.Value.AssemblyNameLocalizationOptions
            .OrderBy(e => e.Order)
            .ToList();

        foreach (var entry in _entries)
        {
            var factory = new ResourceManagerStringLocalizerFactory(
                Options.Create(entry.Options),
                _loggerFactory
            );

            _factories.TryAdd(entry.AssemblyName, factory);
        }
    }

    public IStringLocalizer Create(Type resourceSource)
    {
        var baseName = resourceSource.FullName!;
        var location = resourceSource.Assembly.GetName().Name!;
        return Create(baseName, location);
    }

    public IStringLocalizer Create(string baseName, string location)
    {
        return _compositeCache.GetOrAdd((baseName, location), _ =>
        {
            var localizers = new List<IStringLocalizer>();

            foreach (var entry in _entries)
            {
                if (_factories.TryGetValue(entry.AssemblyName, out var factory))
                {
                    var loc = factory.Create(baseName, entry.AssemblyName.Name!);
                    localizers.Add(loc);
                }
            }

            return new NhCompositeStringLocalizer(
                localizers,
                _loggerFactory.CreateLogger<NhCompositeStringLocalizer>());
        });
    }
}
