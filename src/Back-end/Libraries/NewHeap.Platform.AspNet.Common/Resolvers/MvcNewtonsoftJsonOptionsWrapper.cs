using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace NewHeap.Platform.AspNet.Common.Resolvers;

public partial class MvcNewtonsoftJsonOptionsWrapper : IConfigureOptions<MvcNewtonsoftJsonOptions>
{
    public void Configure(MvcNewtonsoftJsonOptions options)
    {
        options.SerializerSettings.DateFormatString = Platform.Common.Constants.DateTimeOffset.StringFormat;
        options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        options.SerializerSettings.ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = true }
        };
    }
}