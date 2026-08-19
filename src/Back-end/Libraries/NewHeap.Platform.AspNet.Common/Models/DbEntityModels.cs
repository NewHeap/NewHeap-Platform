using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Models;
public partial interface IdDbEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset CreationDateTime { get; set; }
    public DateTimeOffset LastModifiedDateTime { get; set; }
}
