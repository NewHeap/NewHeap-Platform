using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.Common.Exceptions;
public class InvalidFilterCollectionResultException : Exception
{
    public InvalidFilterCollectionResultException(string message) : base(message)
    {
    }
}
