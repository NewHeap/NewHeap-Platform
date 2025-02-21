using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NewHeap.Media;

public class NhMediaContext
{
    public IServiceCollection Services { get; }

    internal NhMediaContext(IServiceCollection services)
    {
        Services = services;
    }
}