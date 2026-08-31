extern alias AutoMapper14;

using System.Collections;
using System.Collections.ObjectModel;
using NewHeap.Platform.Mapping;
using Newtonsoft.Json.Linq;
using AutoMapper14Configuration = AutoMapper14::AutoMapper.MapperConfiguration;
using Xunit;

namespace NewHeap.Platform.Mapping.Tests;

public sealed class AutoMapper14DictionaryAndCollectionParityTests
{
    [Fact]
    public void JObjectMapsToAMutableDictionaryLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = CreateJsonObject();

        var expected = autoMapper.Map<Dictionary<string, JToken>>(source);
        var actual = newHeap.Map<Dictionary<string, JToken>>(source);

        Assert.Equal(expected.Keys, actual.Keys);
        Assert.True(JToken.DeepEquals(expected["name"], actual["name"]));
        Assert.True(JToken.DeepEquals(expected["details"], actual["details"]));
    }

    [Fact]
    public void MutableDictionaryMapsToJObjectLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new Dictionary<string, JToken>
        {
            ["name"] = new JValue("mapped"),
            ["details"] = new JObject { ["enabled"] = true }
        };

        var expected = autoMapper.Map<JObject>(source);
        var actual = newHeap.Map<JObject>(source);

        Assert.True(JToken.DeepEquals(expected, actual));
    }

    [Fact]
    public void ExistingJObjectMemberIsUpdatedInPlaceLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
            configuration.CreateMap<JsonContainerSource, JsonContainerDestination>())
            .CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
            configuration.CreateMap<JsonContainerSource, JsonContainerDestination>())
            .CreateMapper();
        var source = new JsonContainerSource { Payload = CreateJsonObject() };
        var expectedDestination = new JsonContainerDestination
        {
            Payload = new JObject { ["stale"] = true }
        };
        var actualDestination = new JsonContainerDestination
        {
            Payload = new JObject { ["stale"] = true }
        };
        var expectedPayload = expectedDestination.Payload;
        var actualPayload = actualDestination.Payload;

        var expected = autoMapper.Map(source, expectedDestination);
        var actual = newHeap.Map(source, actualDestination);

        Assert.Same(expectedPayload, expected.Payload);
        Assert.Same(actualPayload, actual.Payload);
        Assert.True(JToken.DeepEquals(expected.Payload, actual.Payload));
        Assert.False(actual.Payload.ContainsKey("stale"));
    }

    [Fact]
    public void JValueIsTreatedAsAnAssignableValueLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new JValue("value");

        var expected = autoMapper.Map<JToken>(source);
        var actual = newHeap.Map<JToken>(source);

        Assert.Same(source, expected);
        Assert.Same(source, actual);
    }

    [Fact]
    public void JArrayUsesItsGenericCollectionEntriesLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new JArray("first", new JObject { ["name"] = "second" });

        var expected = autoMapper.Map<JArray>(source);
        var actual = newHeap.Map<JArray>(source);

        Assert.True(JToken.DeepEquals(expected, actual));
        Assert.Equal(ReferenceEquals(source, expected), ReferenceEquals(source, actual));
    }

    [Fact]
    public void GenericCollectionEnumeratorTakesPrecedenceLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new DivergentEnumerable();

        var expected = autoMapper.Map<List<int>>(source);
        var actual = newHeap.Map<List<int>>(source);

        Assert.Equal(expected, actual);
        Assert.Equal([1, 2], actual);
    }

    [Fact]
    public void GenericKeyValueEnumerableUsesItsGenericEntriesLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new DivergentKeyValueEnumerable();

        var expected = autoMapper.Map<Dictionary<string, int>>(source);
        var actual = newHeap.Map<Dictionary<string, int>>(source);

        Assert.Equal(expected, actual);
        Assert.Equal(1, actual["first"]);
        Assert.Equal(2, actual["second"]);
    }

    [Fact]
    public void ConvertibleDictionaryMembersValidateLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
            configuration.CreateMap<ConvertibleDictionarySource, ConvertibleDictionaryDestination>());
        var autoMapper = new AutoMapper14Configuration(configuration =>
            configuration.CreateMap<ConvertibleDictionarySource, ConvertibleDictionaryDestination>());

        autoMapper.AssertConfigurationIsValid();
        newHeap.AssertConfigurationIsValid();
    }

    [Fact]
    public void GenericCollectionMapsToANonGenericListLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new[] { 1, 2 };

        var expected = autoMapper.Map<ArrayList>(source);
        var actual = newHeap.Map<ArrayList>(source);

        Assert.Equal(expected.Cast<int>(), actual.Cast<int>());
    }

    [Fact]
    public void NonGenericListInterfaceMaterializationMatchesAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new[] { 1, 2 };

        var expected = autoMapper.Map<IList>(source);
        var actual = newHeap.Map<IList>(source);

        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Cast<int>(), actual.Cast<int>());
    }

    [Fact]
    public void ExistingNonGenericListIsClearedAndReusedLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new[] { 1, 2 };
        var expectedDestination = new ArrayList { "stale" };
        var actualDestination = new ArrayList { "stale" };

        var expected = autoMapper.Map(source, expectedDestination);
        var actual = newHeap.Map(source, actualDestination);

        Assert.Same(expectedDestination, expected);
        Assert.Same(actualDestination, actual);
        Assert.Equal(expected.Cast<int>(), actual.Cast<int>());
    }

    [Fact]
    public void SetInterfaceMaterializationMatchesAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new[] { 1, 1, 2 };

        var expected = autoMapper.Map<ISet<int>>(source);
        var actual = newHeap.Map<ISet<int>>(source);

        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Order(), actual.Order());
    }

    [Fact]
    public void ReadOnlySetInterfaceMaterializationMatchesAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new[] { 1, 1, 2 };

        var expected = autoMapper.Map<IReadOnlySet<int>>(source);
        var actual = newHeap.Map<IReadOnlySet<int>>(source);

        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Order(), actual.Order());
    }

    [Fact]
    public void ConcreteReadOnlyCollectionMaterializationMatchesAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new[] { 1, 2 };

        var expected = autoMapper.Map<ReadOnlyCollection<int>>(source);
        var actual = newHeap.Map<ReadOnlyCollection<int>>(source);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StandaloneKeyValuePairConvertsLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(_ => { }).CreateMapper();
        var autoMapper = new AutoMapper14Configuration(_ => { }).CreateMapper();
        var source = new KeyValuePair<int, string>(1, "42");

        var expected = autoMapper.Map<KeyValuePair<string, int>>(source);
        var actual = newHeap.Map<KeyValuePair<string, int>>(source);

        Assert.Equal(expected, actual);
        Assert.Equal("1", actual.Key);
        Assert.Equal(42, actual.Value);
    }

    [Fact]
    public void KeyValuePairMembersValidateAndMapLikeAutoMapper14()
    {
        var newHeapConfiguration = new MapperConfiguration(configuration =>
            configuration.CreateMap<KeyValueContainerSource, KeyValueContainerDestination>());
        var autoMapperConfiguration = new AutoMapper14Configuration(configuration =>
            configuration.CreateMap<KeyValueContainerSource, KeyValueContainerDestination>());
        var source = new KeyValueContainerSource
        {
            Item = new KeyValuePair<int, string>(1, "42")
        };

        autoMapperConfiguration.AssertConfigurationIsValid();
        newHeapConfiguration.AssertConfigurationIsValid();
        var expected = autoMapperConfiguration.CreateMapper().Map<KeyValueContainerDestination>(source);
        var actual = newHeapConfiguration.CreateMapper().Map<KeyValueContainerDestination>(source);

        Assert.Equal(expected.Item, actual.Item);
    }

    [Fact]
    public void IncompatibleExistingEnumerableMemberIsReplacedLikeAutoMapper14()
    {
        var newHeap = new MapperConfiguration(configuration =>
            configuration.CreateMap<EnumerableContainerSource, EnumerableContainerDestination>())
            .CreateMapper();
        var autoMapper = new AutoMapper14Configuration(configuration =>
            configuration.CreateMap<EnumerableContainerSource, EnumerableContainerDestination>())
            .CreateMapper();
        var source = new EnumerableContainerSource { Values = [1, 2] };
        var expected = new EnumerableContainerDestination();
        var actual = new EnumerableContainerDestination();

        autoMapper.Map(source, expected);
        newHeap.Map(source, actual);

        Assert.Equal(expected.Values, actual.Values);
        Assert.Equal([1, 2], actual.Values);
    }

    private static JObject CreateJsonObject()
        => new()
        {
            ["name"] = "mapped",
            ["details"] = new JObject { ["enabled"] = true }
        };

    private sealed class JsonContainerSource
    {
        public JObject Payload { get; set; } = new();
    }

    private sealed class JsonContainerDestination
    {
        public JObject Payload { get; set; } = new();
    }

    private sealed class DivergentEnumerable : IEnumerable<int>
    {
        IEnumerator<int> IEnumerable<int>.GetEnumerator()
            => new[] { 1, 2 }.AsEnumerable().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
            => new object[] { new EnumerationSentinel() }.GetEnumerator();
    }

    private sealed class EnumerationSentinel;

    private sealed class DivergentKeyValueEnumerable : IEnumerable<KeyValuePair<string, int>>
    {
        IEnumerator<KeyValuePair<string, int>> IEnumerable<KeyValuePair<string, int>>.GetEnumerator()
            => new Dictionary<string, int>
            {
                ["first"] = 1,
                ["second"] = 2
            }.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator()
            => new object[] { new EnumerationSentinel() }.GetEnumerator();
    }

    private sealed class ConvertibleDictionarySource
    {
        public Dictionary<int, string> Values { get; set; } = [];
    }

    private sealed class ConvertibleDictionaryDestination
    {
        public IReadOnlyDictionary<string, int> Values { get; set; }
            = new Dictionary<string, int>();
    }

    private sealed class KeyValueContainerSource
    {
        public KeyValuePair<int, string> Item { get; set; }
    }

    private sealed class KeyValueContainerDestination
    {
        public KeyValuePair<string, int> Item { get; set; }
    }

    private sealed class EnumerableContainerSource
    {
        public IEnumerable<int> Values { get; set; } = [];
    }

    private sealed class EnumerableContainerDestination
    {
        public IEnumerable<int> Values { get; set; }
            = Enumerable.Range(1, 1).Where(value => value > 0);
    }
}
