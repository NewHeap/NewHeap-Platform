using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.Common;

public enum CRUDActionType
{
    Unknown = 0,
    Create = 1,
    Update = 2,
    Delete = 3,
}

public static partial class Constants
{
    public static class DateTimeOffset
    {
        public const string StringFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";
    }
}
