using System.Collections;
using AwesomeAssertions;
using NewHeap.Platform.AspNet.Common.DAL.Entities;
using NewHeap.Platform.AspNet.Common.Models.View;
using NewHeap.Platform.AspNet.Common.Services.BackgroundOperations;
using NewHeap.Platform.AspNet.Common.Utilities;
using NewHeap.Platform.Mapping;
using Xunit;

namespace NewHeap.Platform.AspNet.Common.Tests;

public sealed class NhBackgroundOperationSummaryProjectionTests
{
    [Fact]
    public void SummaryProjectionKeepsEverySummaryFieldAndDropsThePayload()
    {
        var operation = CreateOperationWithEveryScalarSet();
        var mapper = new Mapper(new MapperConfiguration(configuration =>
            configuration.AddProfile<AutomapperProfileConfiguration>()));

        var summary = new[] { operation }.AsQueryable().SelectSummary().Single();

        summary.PayloadJson.Should().Be("{}");
        var expected = mapper.Map<NhBackgroundOperationAdministrationViewModel>(operation);
        var actual = mapper.Map<NhBackgroundOperationAdministrationViewModel>(summary);
        foreach (var property in typeof(NhBackgroundOperationAdministrationViewModel).GetProperties())
        {
            if (typeof(IEnumerable).IsAssignableFrom(property.PropertyType) && property.PropertyType != typeof(string))
            {
                continue;
            }

            property.GetValue(actual).Should().Be(
                property.GetValue(expected),
                $"the summary projection must select {property.Name}");
        }
    }

    private static NhBackgroundOperation CreateOperationWithEveryScalarSet()
    {
        var operation = new NhBackgroundOperation();
        var index = 1;
        foreach (var property in typeof(NhBackgroundOperation).GetProperties().Where(x => x.CanWrite))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object? value = type switch
            {
                _ when type == typeof(Guid) => Guid.NewGuid(),
                _ when type == typeof(string) => $"{property.Name}-{index}",
                _ when type == typeof(int) => index,
                _ when type == typeof(long) => (long)index,
                _ when type == typeof(decimal) => index + .5m,
                _ when type == typeof(DateTimeOffset) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index),
                _ when type.IsEnum => Enum.GetValues(type).Cast<object>().Last(),
                _ => null
            };
            if (value is not null)
            {
                property.SetValue(operation, value);
            }

            index++;
        }

        operation.PayloadJson = "{\"secret\":true}";
        return operation;
    }
}
