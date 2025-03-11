using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.Common.Utilities;
public static partial class GlobalizationUtils
{
    public static void TaskWithCulture(CultureInfo culture, Action task, CultureInfo? uiCulture = null)
    {
        if (culture == null || task == null)
        {
            throw new ArgumentNullException();
        }

        uiCulture ??= culture;
        var currentCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
        var currentUICulture = System.Threading.Thread.CurrentThread.CurrentUICulture;

        System.Threading.Thread.CurrentThread.CurrentCulture = culture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = uiCulture;

        task();

        System.Threading.Thread.CurrentThread.CurrentCulture = currentCulture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = currentUICulture;
    }

    public static async Task TaskWithCultureAsync(CultureInfo culture, Func<Task> task, CultureInfo? uiCulture = null)
    {
        if (culture == null || task == null)
        {
            throw new ArgumentNullException();
        }

        uiCulture ??= culture;
        var currentCulture = System.Threading.Thread.CurrentThread.CurrentCulture;
        var currentUICulture = System.Threading.Thread.CurrentThread.CurrentUICulture;

        System.Threading.Thread.CurrentThread.CurrentCulture = culture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = uiCulture;

        await task();

        System.Threading.Thread.CurrentThread.CurrentCulture = currentCulture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = currentUICulture;
    }
}
