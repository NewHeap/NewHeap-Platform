using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Models.Options;

public class NewHeapAspNetCommonSettings
{
    public string DefaultCulture { get; set; } = "";

    public string[] SupportedCultures { get; set; } = [];
}
