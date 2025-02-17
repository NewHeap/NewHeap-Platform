using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AspNet.Common.Models.Options;
using NewHeap.Platform.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common
{
    public class NewHeapPlatformAspNetCommonConfigurator
    {
        private readonly IServiceCollection _serviceCollection;
        private readonly NewHeapPlatformCommonConfigurator _commonConfigurator;
        private readonly NewHeapAspNetCommonOptions _options;

        public NewHeapPlatformAspNetCommonConfigurator(
            IServiceCollection serviceCollection,
            NewHeapPlatformCommonConfigurator commonConfigurator,
            NewHeapAspNetCommonOptions options
        )
        {
            _serviceCollection = serviceCollection;
            _commonConfigurator = commonConfigurator;
            _options = options;

            AddDefault();
        }

        private void AddDefault()
        {
            //Must register the options object as a singleton so it can be injected into the DbContext etc.
            _serviceCollection.AddSingleton(_options);
        }

        public NewHeapPlatformAspNetCommonConfigurator ConfigureCommon(Action<NewHeapPlatformCommonConfigurator> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            action.Invoke(_commonConfigurator);

            return this;
        }

        public NewHeapPlatformAspNetCommonConfigurator WithThisIsAPlaceholder()
        {
            //_serviceCollection.AddTransient<IEmailService, EmailService>();
            return this;
        }
    }
}
