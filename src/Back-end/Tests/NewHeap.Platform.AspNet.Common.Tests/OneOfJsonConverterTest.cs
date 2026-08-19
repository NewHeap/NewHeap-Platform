using NewHeap.Platform.AspNet.Common.Converters;
using Newtonsoft.Json;
using OneOf;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public class OneOfJsonConverterTest
{
    [Theory]
    [InlineData("\"value\"", "value", 0)]
    [InlineData("42", null, 42)]
    public void DeserializeObject_DeserializesMatchingOneOfVariant(string json, string? expectedString, int expectedInt)
    {
        var value = JsonConvert.DeserializeObject<OneOf<string, int>>(json, new OneOfJsonConverter());

        if (expectedString is not null)
        {
            Assert.True(value.IsT0);
            Assert.Equal(expectedString, value.AsT0);
        }
        else
        {
            Assert.True(value.IsT1);
            Assert.Equal(expectedInt, value.AsT1);
        }
    }

    [Fact]
    public void DeserializeObject_DeserializesOneOfBaseSubclass()
    {
        var value = JsonConvert.DeserializeObject<StringOrInt>("42", new OneOfJsonConverter())!;

        Assert.True(value.IsT1);
        Assert.Equal(42, value.AsT1);
    }

    private class StringOrInt : OneOfBase<string, int>
    {
        private StringOrInt(OneOf<string, int> input) : base(input)
        {
        }

        public static implicit operator StringOrInt(string input) => new(OneOf<string, int>.FromT0(input));

        public static implicit operator StringOrInt(int input) => new(OneOf<string, int>.FromT1(input));
    }
}
