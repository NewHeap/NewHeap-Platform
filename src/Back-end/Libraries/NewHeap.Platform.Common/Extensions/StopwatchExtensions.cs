
using System.Diagnostics;

namespace NewHeap.Platform.Common;

public static partial class StopwatchExtensions
{
    /// <summary>
    /// Stops the <see cref="Stopwatch"/> and returns the elapsed time.
    /// Allows a more fluent api for <see cref="Stopwatch"/>
    /// </summary>
    /// <param name="sw"></param>
    /// <returns><see cref="Stopwatch.Elapsed"/></returns>
    public static TimeSpan StopElapsed(this Stopwatch sw)
    {
        sw.Stop();
        return sw.Elapsed;
    }
}