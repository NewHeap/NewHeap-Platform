using NSubstitute;
using NSubstitute.Core;
using System.Linq.Expressions;

namespace NewHeap.Platform.Common.Test.Extensions;

public static class TestExtensions
{
    public static ConfiguredCall ReturnsAny<T>(this Task<bool> returnThis, IEnumerable<T> collection)
    {
        return returnThis.Returns(
            callInfo => collection.Any(callInfo.Arg<Expression<Func<T, bool>>>().Compile())
        );
    }

    public static ConfiguredCall ReturnsFirstOrDefault<T>(this Task<T?> returnThis, IEnumerable<T> collection)
    {
        return returnThis.Returns(
            callInfo => collection.FirstOrDefault(callInfo.Arg<Expression<Func<T, bool>>>().Compile())
        );
    }


    public static ConfiguredCall ReturnsCount<T>(this Task<int> returnThis, IEnumerable<T> collection)
    {
        return returnThis.Returns(
            callInfo => collection.Count(callInfo.Arg<Expression<Func<T, bool>>>().Compile())
        );
    }
}
