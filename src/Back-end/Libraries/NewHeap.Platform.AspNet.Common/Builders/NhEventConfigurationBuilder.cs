using Microsoft.Extensions.DependencyInjection;

namespace NewHeap.Platform.AspNet.Common.Builders;

public class NhEventConfigurationBuilder
{
    public readonly IServiceCollection ServiceCollection;

    public NhEventConfigurationBuilder(IServiceCollection serviceCollection)
    {
        ServiceCollection = serviceCollection;
    }
}