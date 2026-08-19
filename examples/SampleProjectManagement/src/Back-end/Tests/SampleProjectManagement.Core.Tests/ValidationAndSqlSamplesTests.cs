using System.ComponentModel.DataAnnotations;
using NewHeap.Platform.AspNet.Common.DAL;
using NewHeap.Platform.Common.Attributes;
using Xunit;

namespace SampleProjectManagement.Core.Tests;

public class ValidationAndSqlSamplesTests
{
    [Theory]
    [InlineData(11d, true)]
    [InlineData(10d, false)]
    [InlineData(null, false)]
    public void GreaterThanValidationHasAnExplicitNullPolicy(double? value, bool expected)
    {
        var attribute = new NhGreaterThanAttribute(10);
        var context = new ValidationContext(new object()) { DisplayName = "budget" };
        Assert.Equal(expected, attribute.GetValidationResult(value, context) is null);
    }

    [Theory]
    [InlineData(9, true)]
    [InlineData(10, false)]
    public void LessThanValidationUsesStrictComparison(double value, bool expected)
    {
        var attribute = new NhLessThanAttribute(10);
        var context = new ValidationContext(new object()) { DisplayName = "risk" };
        Assert.Equal(expected, attribute.GetValidationResult(value, context) is null);
    }

    [Fact]
    public void RawSqlMarkerIsExplicitAndOrdinaryValuesRemainParameters()
    {
        var column = "Name".Raw();
        var userInput = "NHP'; DROP TABLE Projects; --";
        FormattableString query = $"SELECT {column} FROM Projects WHERE Name = {userInput}";

        Assert.Equal("Name", column.Value);
        Assert.Equal(2, query.ArgumentCount);
        Assert.Same(userInput, query.GetArgument(1));
    }
}
