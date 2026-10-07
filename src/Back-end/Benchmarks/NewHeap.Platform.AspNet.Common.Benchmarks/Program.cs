using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.AspNetCore.Mvc;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Resolvers;
using NewHeap.Platform.AspNet.Common.Services;
using Newtonsoft.Json;

BenchmarkSwitcher.FromAssembly(typeof(CollectionEncodingBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
[ShortRunJob]
public class CollectionEncodingBenchmarks
{
    [Params(6, 18)]
    public int FieldCount { get; set; }

    private NhSelectedCollectionResult _result = null!;
    private JsonSerializerSettings _settings = null!;

    [GlobalSetup]
    public void Setup()
    {
        var options = new MvcNewtonsoftJsonOptions();
        new MvcNewtonsoftJsonOptionsWrapper().Configure(options);
        _settings = options.SerializerSettings;
        var names = Enumerable.Range(0, FieldCount).Select(index => $"field{index}").ToArray();
        _result = new NhSelectedCollectionResult
        {
            Page = 1, ItemsPerPage = 50, ResultCount = 50, TotalCount = 1000,
            Selection = new NhCollectionSelection(names, names, "json"),
            Items = Enumerable.Range(0, 50).Select(row => names.Select((name, index) => (name, value: (object?)((index % 4) switch
            {
                0 => (object)$"Project {row} value {index}",
                1 => 123.45m + row,
                2 => false,
                _ => new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero)
            }))).ToDictionary(item => item.name, item => item.value)).ToList()
        };
        Console.WriteLine($"Payload bytes ({FieldCount} fields, 50 rows): JSON={Json().Length}, MessagePack={MessagePack().Length}");
    }

    [Benchmark(Baseline = true)]
    public byte[] Json()
    {
        return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_result, _settings));
    }

    [Benchmark]
    public byte[] MessagePack()
    {
        return NhSelectedCollectionMessagePack.Serialize(_result, _settings);
    }
}
