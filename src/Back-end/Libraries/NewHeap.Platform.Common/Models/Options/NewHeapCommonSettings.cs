using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NewHeap.Platform.Common.Models.Options;

public class NewHeapCommonOptions
{
    public required Action<NewHeapCommonSettings> SettingsAction { get; set; }
}

public class NewHeapCommonSettings
{
}
