using DotNetCore.CAP;
using Microsoft.EntityFrameworkCore.Storage;

namespace NewHeap.Platform.Events.Cap;

public class CapTransactionScope
{
    public ICapTransaction? Current { get; set; }

    internal bool IsCommitStarted { get; set; } = false;
}
