extern alias AutoMapper14;

using Xunit;
using AM = AutoMapper14::AutoMapper;

namespace NewHeap.Platform.Mapping.Tests;

public sealed class PublicFieldParityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("Description")]
    public void StructFieldsAndNullValuesMatchAutoMapper14(string? description)
    {
        var reference = new AM.MapperConfiguration(c => c.CreateMap<Source, ValueView>()).CreateMapper();
        var mapper = new MapperConfiguration(c => c.CreateMap<Source, ValueView>()).CreateMapper();
        var source = new Source { Description = description };
        Assert.Equal(reference.Map<ValueView>(source).Description, mapper.Map<ValueView>(source).Description);
    }

    [Fact]
    public void ConventionMappingPopulatesInheritedFieldsAndPreservesReadOnlyFields()
    {
        var reference = new AM.MapperConfiguration(c => c.CreateMap<Source, View>());
        var configuration = new MapperConfiguration(c => c.CreateMap<Source, View>());
        reference.AssertConfigurationIsValid();
        configuration.AssertConfigurationIsValid();
        var source = new Source { Description = "Available description", Count = 42, ReadOnly = "replace" };

        var expected = reference.CreateMapper().Map<View>(source);
        var actual = configuration.CreateMapper().Map<View>(source);

        Assert.Equal("Available description", actual.Description);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Count, actual.Count);
        Assert.Equal(expected.ReadOnly, actual.ReadOnly);
    }

    [Fact]
    public void ExplicitFieldConfigurationAndConditionsMatchAutoMapper14()
    {
        var reference = new AM.MapperConfiguration(c => c.CreateMap<Source, View>()
            .ForMember(d => d.Description, o => o.MapFrom(s => s.Description + " mapped"))
            .ForMember(d => d.Count, o => o.Ignore())).CreateMapper();
        var mapper = new MapperConfiguration(c => c.CreateMap<Source, View>()
            .ForMember(d => d.Description, o => o.MapFrom(s => s.Description + " mapped"))
            .ForMember(d => d.Count, o => o.Ignore())).CreateMapper();
        var source = new Source { Description = "Text", Count = 99 };
        var expected = reference.Map(source, new View { Count = 7 });
        var actual = mapper.Map(source, new View { Count = 7 });
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.Count, actual.Count);

        var guarded = new MapperConfiguration(c => c.CreateMap<Source, View>()
            .ForAllMembers(o => o.Condition((_, _, _, _) => false))).CreateMapper();
        var destination = new View { Description = "Keep", Count = 7 };
        guarded.Map(source, destination);
        Assert.Equal("Keep", destination.Description);
        Assert.Equal(7, destination.Count);
    }

    [Fact]
    public void FieldCollectionsAndCyclesPreserveIdentity()
    {
        var reference = new AM.MapperConfiguration(c => c.CreateMap<Node, NodeView>()).CreateMapper();
        var mapper = new MapperConfiguration(c => c.CreateMap<Node, NodeView>()).CreateMapper();
        var source = new Node();
        source.Children.Add(source);
        var expected = reference.Map<NodeView>(source);
        var actual = mapper.Map<NodeView>(source);
        Assert.Same(expected, Assert.Single(expected.Children));
        Assert.Same(actual, Assert.Single(actual.Children));
    }

    [Fact]
    public void FieldsParticipateInConfigurationValidation()
    {
        var reference = new AM.MapperConfiguration(c => c.CreateMap<object, View>());
        var configuration = new MapperConfiguration(c => c.CreateMap<object, View>());
        Assert.Throws<AM.AutoMapperConfigurationException>(reference.AssertConfigurationIsValid);
        var exception = Assert.Throws<MappingConfigurationException>(configuration.AssertConfigurationIsValid);
        Assert.Contains(nameof(View.Description), exception.Message);
        Assert.Contains(nameof(View.Count), exception.Message);
    }

    [Fact]
    public void IncludeBaseCarriesExplicitFieldConfiguration()
    {
        var mapper = new MapperConfiguration(c =>
        {
            c.CreateMap<Source, BaseView>().ForMember(d => d.Description, o => o.MapFrom(s => "Mapped " + s.Description));
            c.CreateMap<Source, View>().IncludeBase<Source, BaseView>();
        }).CreateMapper();
        Assert.Equal("Mapped text", mapper.Map<View>(new Source { Description = "text" }).Description);
    }

    public sealed class Source
    {
        public string? Description { get; set; }
        public int Count;
        public string? ReadOnly { get; set; }
    }

    public struct ValueView
    {
        public string? Description;
    }

    public class BaseView
    {
        public string? Description;
    }

    public sealed class View : BaseView
    {
        public int Count;
        public readonly string ReadOnly = "Keep";
    }

    public sealed class Node
    {
        public List<Node> Children = [];
    }

    public sealed class NodeView
    {
        public List<NodeView> Children = [];
    }
}
