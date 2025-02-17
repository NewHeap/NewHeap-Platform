using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Serialization;

namespace NewHeap.Platform.AspNet.Policy.Resolvers;

public partial class MvcNewtonsoftJsonOptionsWrapper : IConfigureOptions<MvcNewtonsoftJsonOptions>
{
    public MvcNewtonsoftJsonOptionsWrapper()
    {
    }

    public void Configure(MvcNewtonsoftJsonOptions options)
    {
        options.SerializerSettings.DateFormatString = Platform.Common.Constants.DateTimeOffset.StringFormat;
        options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        options.SerializerSettings.ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy
            {
                ProcessDictionaryKeys = true
            }
        };
    }
}
