using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.Common.Extensions;
using NewHeap.Platform.AspNet.Common.Extensions;

namespace WebAPI
{
    public class Startup
    {
        private readonly IWebHostEnvironment _currentEnvironment;

        public Startup(IConfiguration configuration, IWebHostEnvironment env)
        {
            _currentEnvironment = env;
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddNewHeapPlatformAspNetCommon(options =>
            {
                //options.
            })
            .ConfigureCommon(commonConfig =>
            {
                commonConfig
                    .WithMail(Configuration.GetSection("EmailSettings"))
                ;
            })
            .WithThisIsAPlaceholder()
            ;
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, IServiceProvider services)
        {
            app.UseNewHeapPlatformAspNetCommon();
        }
    }
}
