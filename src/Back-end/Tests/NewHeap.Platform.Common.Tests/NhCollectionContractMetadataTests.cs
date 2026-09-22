using NewHeap.Platform.Common.Attributes;
using NewHeap.Platform.Common.Models;
using Xunit;

namespace NewHeap.Platform.Common.Tests;

public sealed class NhCollectionContractMetadataTests
{
    [Fact]
    public void Descriptor_uses_runtime_attributes_types_and_operator_vocabulary()
    {
        var contract = NhCollectionContractMetadata.Describe(typeof(CollectionItem));

        var status = Assert.Single(contract.FilterFields, field => field.Name == "status");
        Assert.Equal(NhCollectionFieldType.Enum, status.FieldType);
        Assert.Equal(["Draft", "Done"], status.EnumValues);
        Assert.Equal(NhCollectionContractMetadata.SupportedFilterOperators, status.Operators);
        Assert.Contains(contract.OrderFields, field => field.Name == "createdAt");
        Assert.Contains(contract.SearchFields, field => field.Name == "title");
        Assert.Equal(["id", "title", "status", "createdAt"], contract.ResultFields.Select(field => field.Name));
    }

    [Theory]
    [InlineData(typeof(CollectionResultModel<CollectionItem>))]
    [InlineData(typeof(SimpleCollectionResultModel<CollectionItem>))]
    [InlineData(typeof(DerivedResult))]
    public void Canonical_result_variants_resolve_the_item_type(Type resultType)
    {
        Assert.True(NhCollectionContractMetadata.TryGetItemType(resultType, out var itemType));
        Assert.Equal(typeof(CollectionItem), itemType);
    }

    [Theory]
    [InlineData("==")]
    [InlineData("like")]
    [InlineData("NOT IN")]
    public void Runtime_operator_check_uses_the_canonical_vocabulary(string value)
    {
        Assert.True(NhCollectionContractMetadata.IsSupportedFilterOperator(value));
    }

    private sealed class DerivedResult : CollectionResultModel<CollectionItem>
    {
    }

    private sealed class CollectionItem
    {
        public int Id { get; init; }

        [Searchable]
        public string Title { get; init; } = string.Empty;

        [Filterable]
        public Status Status { get; init; }

        [Orderable]
        public DateTimeOffset CreatedAt { get; init; }
    }

    private enum Status
    {
        Draft = 0,
        Done = 1
    }
}
