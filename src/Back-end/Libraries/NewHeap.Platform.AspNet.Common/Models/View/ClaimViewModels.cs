using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Models.View;

public partial class ClaimViewModel
{
    public partial class Property
    { 
        public string Key { get; set; }
        public string Value { get; set; }
    }

    public string Type { get; set; }
    public string Value { get; set; }
}
