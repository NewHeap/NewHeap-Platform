extern alias AutoMapper14;

using Xunit;
using AM = AutoMapper14::AutoMapper;

namespace NewHeap.Platform.Mapping.Tests;

public sealed class NullTypeConverterParityTests
{
    [Fact]
    public void ClassConvertersReceiveNullRootsAndMembersLikeAutoMapper14()
    {
        var reference = new AM.MapperConfiguration(configuration =>
        {
            configuration.CreateMap<ContentData, ContentDataView>().ConvertUsing<DataConverter>();
            configuration.CreateMap<Content, ContentView>();
        }).CreateMapper();
        var mapper = new MapperConfiguration(configuration =>
        {
            configuration.CreateMap<ContentData, ContentDataView>().ConvertUsing<DataConverter>();
            configuration.CreateMap<Content, ContentView>();
        }).CreateMapper(type => Activator.CreateInstance(type));

        var expectedRoot = reference.Map<ContentData, ContentDataView>(null!);
        var actualRoot = mapper.Map<ContentData, ContentDataView>(null!);
        var expectedMember = reference.Map<ContentView>(new Content());
        var actualMember = mapper.Map<ContentView>(new Content());

        Assert.Equal("missing", expectedRoot.Value);
        Assert.Equal(expectedRoot.Value, actualRoot.Value);
        Assert.Equal("missing", expectedMember.Data!.Value);
        Assert.Equal(expectedMember.Data.Value, actualMember.Data!.Value);
    }

    public sealed class DataConverter : ITypeConverter<ContentData, ContentDataView>,
        AM.ITypeConverter<ContentData, ContentDataView>
    {
        public ContentDataView Convert(ContentData source, ContentDataView destination, ResolutionContext context)
        {
            return new ContentDataView { Value = source?.Value ?? "missing" };
        }

        public ContentDataView Convert(ContentData source, ContentDataView destination, AM.ResolutionContext context)
        {
            return new ContentDataView { Value = source?.Value ?? "missing" };
        }
    }

    public sealed class ContentData
    {
        public string Value { get; set; } = string.Empty;
    }

    public sealed class ContentDataView
    {
        public string Value { get; set; } = string.Empty;
    }

    public sealed class Content
    {
        public ContentData? Data { get; set; }
    }

    public sealed class ContentView
    {
        public ContentDataView? Data { get; set; }
    }
}
