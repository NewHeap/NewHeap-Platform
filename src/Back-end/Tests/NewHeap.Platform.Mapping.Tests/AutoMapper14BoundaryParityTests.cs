extern alias AutoMapper14;

using System.Globalization;
using System.Runtime.Serialization;
using NewHeap.Platform.Mapping;
using Newtonsoft.Json;
using Xunit;
using AM = AutoMapper14::AutoMapper;

namespace NewHeap.Platform.Mapping.Tests;

public sealed class AutoMapper14BoundaryParityTests
{
    [Theory]
    [InlineData(typeof(NodeView[]))]
    [InlineData(typeof(List<NodeView>))]
    [InlineData(typeof(IEnumerable<NodeView>))]
    [InlineData(typeof(Dictionary<string, NodeView>))]
    public void TopLevelCollectionReferenceScopeMatchesAutoMapper14(Type destinationType)
    {
        var reference = new AM.MapperConfiguration(c => c.CreateMap<Node, NodeView>()).CreateMapper();
        var mapper = new MapperConfiguration(c => c.CreateMap<Node, NodeView>()).CreateMapper();
        var node = new Node();
        node.Child = node;
        object source = destinationType == typeof(Dictionary<string, NodeView>)
            ? new Dictionary<string, Node> { ["first"] = node, ["second"] = node }
            : new[] { node, node };

        var expected = reference.Map(source, source.GetType(), destinationType);
        var actual = mapper.Map(source, source.GetType(), destinationType);

        Assert.NotNull(actual);
        Assert.Equal(SharesReference(expected), SharesReference(actual));
    }

    [Fact]
    public void NonRecursiveMapsDoNotDeduplicateSharedMembers()
    {
        var reference = new AM.MapperConfiguration(c =>
        {
            c.CreateMap<PairSource, PairView>();
            c.CreateMap<ValueSource, ValueView>();
        }).CreateMapper();
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<PairSource, PairView>();
            c.CreateMap<ValueSource, ValueView>();
        }).CreateMapper();
        var value = new ValueSource { Value = "shared" };
        var source = new PairSource { First = value, Second = value };

        var expected = reference.Map<PairView>(source);
        var actual = mapper.Map<PairView>(source);

        Assert.Equal(ReferenceEquals(expected.First, expected.Second), ReferenceEquals(actual.First, actual.Second));
        Assert.NotSame(actual.First, actual.Second);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CollectionDepthAppliesToItsOwningMapAndPreservesArrayBehavior(int depth)
    {
        var reference = new AM.MapperConfiguration(c => c.CreateMap<Node, NodeView>().MaxDepth(depth)).CreateMapper();
        var mapper = new MapperConfiguration(c => c.CreateMap<Node, NodeView>().MaxDepth(depth)).CreateMapper();
        var source = new Node
        {
            Child = new Node { Names = ["child"] },
            Children = [new Node { Names = ["collection-child"] }],
            Array = [new Node { Names = ["array-child"] }],
            Names = ["root"]
        };

        Assert.Equal(JsonConvert.SerializeObject(reference.Map<NodeView>(source)),
            JsonConvert.SerializeObject(mapper.Map<NodeView>(source)));
    }

    [Fact]
    public void ConvertersReceiveNullCollectionElementsAndExistingMembers()
    {
        var reference = new AM.MapperConfiguration(c =>
        {
            c.CreateMap<ValueSource, ValueView>();
            c.CreateMap<string, string>().ConvertUsing(source => source ?? "fallback");
        }).CreateMapper();
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<ValueSource, ValueView>();
            c.CreateMap<string, string>().ConvertUsing(source => source ?? "fallback");
        }).CreateMapper();

        Assert.Equal(reference.Map<string[]>(new string?[] { null, "present" }),
            mapper.Map<string[]>(new string?[] { null, "present" }));
        Assert.Equal(reference.Map(new ValueSource(), new ValueView { Value = "old" }).Value,
            mapper.Map(new ValueSource(), new ValueView { Value = "old" }).Value);
    }

    [Fact]
    public void NullCollectionConvertersOverrideEmptyCollectionMaterialization()
    {
        var reference = new AM.MapperConfiguration(c => c.CreateMap<string[], List<string>>()
            .ConvertUsing(source => source == null ? new List<string> { "fallback" } : source.ToList())).CreateMapper();
        var mapper = new MapperConfiguration(c => c.CreateMap<string[], List<string>>()
            .ConvertUsing(source => source == null ? new List<string> { "fallback" } : source.ToList())).CreateMapper();

        Assert.Equal(reference.Map<string[], List<string>>(null!), mapper.Map<string[], List<string>>(null!));
    }

    [Theory]
    [InlineData("nl-NL", "1,25")]
    [InlineData("en-US", "1.25")]
    [InlineData("de-DE", "1.234,50")]
    public void NullableNumericMembersAndValidationFollowCurrentCulture(string culture, string input)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var reference = new AM.MapperConfiguration(c => c.CreateMap<ValueSource, NumericView>()).CreateMapper();
            var configuration = new MapperConfiguration(c => c.CreateMap<ValueSource, NumericView>());
            configuration.AssertConfigurationIsValid();
            var mapper = configuration.CreateMapper();

            Assert.Equal(reference.Map<NumericView>(new ValueSource { Value = input }).Value,
                mapper.Map<NumericView>(new ValueSource { Value = input }).Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData(SourceState.Read)]
    [InlineData(SourceState.Read | SourceState.Write)]
    [InlineData((SourceState)64)]
    public void EnumNamesFlagsAndUndefinedValuesMatchAutoMapper14(SourceState value)
    {
        var reference = new AM.MapperConfiguration(_ => { }).CreateMapper();
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();

        Assert.Equal(reference.Map<DestinationState>(value), mapper.Map<DestinationState>(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("read-wire")]
    [InlineData("READ-WIRE")]
    [InlineData("Read, Write")]
    [InlineData("64")]
    public void EnumWireValuesAndEmptyValuesMatchAutoMapper14(string value)
    {
        var reference = new AM.MapperConfiguration(_ => { }).CreateMapper();
        var mapper = new MapperConfiguration(_ => { }).CreateMapper();

        Assert.Equal(reference.Map<DestinationState>(value), mapper.Map<DestinationState>(value));
    }

    [Fact]
    public void FlatteningNullsAndInheritedPrivateSettersValidateAndMap()
    {
        var referenceConfiguration = new AM.MapperConfiguration(c => c.CreateMap<FlattenedSource, FlattenedView>());
        var configuration = new MapperConfiguration(c => c.CreateMap<FlattenedSource, FlattenedView>());
        referenceConfiguration.AssertConfigurationIsValid();
        configuration.AssertConfigurationIsValid();
        var source = new FlattenedSource { Owner = new PairSource { First = new ValueSource { Value = "nested" } } };

        Assert.Equal(JsonConvert.SerializeObject(referenceConfiguration.CreateMapper().Map<FlattenedView>(source)),
            JsonConvert.SerializeObject(configuration.CreateMapper().Map<FlattenedView>(source)));
        Assert.Equal(JsonConvert.SerializeObject(referenceConfiguration.CreateMapper().Map<FlattenedView>(new FlattenedSource())),
            JsonConvert.SerializeObject(configuration.CreateMapper().Map<FlattenedView>(new FlattenedSource())));
    }

    [Fact]
    public void IncludeBaseRejectsAnIncompatibleExistingDestinationBeforeWriting()
    {
        var reference = new AM.MapperConfiguration(c =>
        {
            c.CreateMap<BaseSource, BaseView>();
            c.CreateMap<DerivedSource, DerivedView>().IncludeBase<BaseSource, BaseView>();
        }).CreateMapper();
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<BaseSource, BaseView>();
            c.CreateMap<DerivedSource, DerivedView>().IncludeBase<BaseSource, BaseView>();
        }).CreateMapper();
        var source = new DerivedSource { Name = "base", Detail = "derived" };
        var existing = new BaseView { Name = "unchanged" };
        Assert.Throws<InvalidCastException>(() => reference.Map(source, new BaseView()));
        Assert.Throws<MappingException>(() => mapper.Map(source, existing));
        Assert.Equal("unchanged", existing.Name);

        BaseView expectedDerived = new DerivedView();
        BaseView actualDerived = new DerivedView();
        Assert.Same(expectedDerived, reference.Map(source, expectedDerived));
        Assert.Same(actualDerived, mapper.Map(source, actualDerived));
        Assert.Equal(JsonConvert.SerializeObject(expectedDerived), JsonConvert.SerializeObject(actualDerived));
    }

    [Fact]
    public void ScalarParsingMembersPassConfigurationValidation()
    {
        var configuration = new MapperConfiguration(c => c.CreateMap<ParsedSource, ParsedView>());
        var referenceConfiguration = new AM.MapperConfiguration(c => c.CreateMap<ParsedSource, ParsedView>());
        configuration.AssertConfigurationIsValid();
        referenceConfiguration.AssertConfigurationIsValid();
        var source = new ParsedSource
        {
            Id = "00112233-4455-6677-8899-aabbccddeeff",
            Duration = "01:02:03",
            Timestamp = "2026-01-01T00:00:00+00:00"
        };

        Assert.Equal(JsonConvert.SerializeObject(referenceConfiguration.CreateMapper().Map<ParsedView>(source)),
            JsonConvert.SerializeObject(configuration.CreateMapper().Map<ParsedView>(source)));
    }

    private static bool SharesReference(object mapped)
    {
        var values = mapped is IDictionary<string, NodeView> dictionary
            ? dictionary.Values.ToArray()
            : ((IEnumerable<NodeView>)mapped).ToArray();
        Assert.Same(values[0], values[0].Child);
        return ReferenceEquals(values[0], values[1]);
    }

    public sealed class Node
    {
        public Node? Child { get; set; }
        public List<Node> Children { get; set; } = [];
        public Node[] Array { get; set; } = [];
        public List<string> Names { get; set; } = [];
    }

    public sealed class NodeView
    {
        public NodeView? Child { get; set; }
        public List<NodeView> Children { get; set; } = [];
        public NodeView[] Array { get; set; } = [];
        public List<string> Names { get; set; } = [];
    }

    public sealed class ValueSource
    {
        public string? Value { get; set; }
    }

    public sealed class ValueView
    {
        public string? Value { get; set; }
    }

    public sealed class NumericView
    {
        public decimal? Value { get; set; }
    }

    public sealed class PairSource
    {
        public ValueSource? First { get; set; }
        public ValueSource? Second { get; set; }
    }

    public sealed class PairView
    {
        public ValueView? First { get; set; }
        public ValueView? Second { get; set; }
    }

    public sealed class FlattenedSource
    {
        public PairSource? Owner { get; set; }
        public string GetLabel() => "label";
    }

    public class PrivateSetterView
    {
        public string? Label { get; private set; }
    }

    public sealed class FlattenedView : PrivateSetterView
    {
        public string? OwnerFirstValue { get; private set; }
    }

    public class BaseSource
    {
        public string? Name { get; set; }
    }

    public sealed class DerivedSource : BaseSource
    {
        public string? Detail { get; set; }
    }

    public class BaseView
    {
        public string? Name { get; set; }
    }

    public sealed class DerivedView : BaseView
    {
        public string? Detail { get; set; }
    }

    public sealed class ParsedSource
    {
        public string? Id { get; set; }
        public string? Duration { get; set; }
        public string? Timestamp { get; set; }
    }

    public sealed class ParsedView
    {
        public Guid? Id { get; set; }
        public TimeSpan? Duration { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
    }

    [Flags]
    public enum SourceState
    {
        Read = 1,
        Write = 2
    }

    [Flags]
    public enum DestinationState
    {
        [EnumMember(Value = "read-wire")]
        Read = 4,
        Write = 8
    }
}
