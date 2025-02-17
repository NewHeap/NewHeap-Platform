using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Models.Options;

public class DbLogSettings
{
    /// <summary>
    /// The directory where to store files which will be relatively referenced from the database entry
    /// </summary>
    public string RootDirectory { get; set; } = "";

    /// <summary>
    /// Email addresses to mail when an uncaught error occurs
    /// </summary>
    public string[] ErrorMailAddresses { get; set; } = [];
}
