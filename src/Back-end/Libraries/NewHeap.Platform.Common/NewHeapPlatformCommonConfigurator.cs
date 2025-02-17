using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Models.Options;
using NewHeap.Platform.Common.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.Common
{
    public class NewHeapPlatformCommonConfigurator
    {
        private readonly IServiceCollection _serviceCollection;
        private readonly NewHeapCommonOptions _options;

        public NewHeapPlatformCommonConfigurator(
            IServiceCollection serviceCollection,
            NewHeapCommonOptions options
            )
        {
            _serviceCollection = serviceCollection;
            _options = options;

            AddDefault();
        }

        private void AddDefault()
        {
            //Must register the options object as a singleton so it can be injected into the DbContext etc.
            _serviceCollection.AddSingleton(_options);

            _serviceCollection.AddScoped<LogHelperService>();
            _serviceCollection.AddScoped<ValidationService>();
        }

        public NewHeapPlatformCommonConfigurator WithMail(IConfiguration mailServiceSettings)
        {
            _serviceCollection.Configure<MailServiceSettings>(mailServiceSettings);
            _serviceCollection.AddScoped<MailService, MailService>();
            return this;
        }

        public NewHeapPlatformCommonConfigurator WithMicrosoftAuth(IConfiguration microsoftAuthSettings)
        {
            _serviceCollection.Configure<MicrosoftAuthSettings>(microsoftAuthSettings);
            _serviceCollection.AddScoped<MicrosoftAuthService, MicrosoftAuthService>();
            return this;
        }
    }
}
