using Microsoft.AspNetCore.Builder;

namespace NewHeap.Platform.AspNet.Common.Utilities;

public interface IStartupConfiguration
{
    public void Configure(IApplicationBuilder app, IServiceProvider serviceProvider);
}