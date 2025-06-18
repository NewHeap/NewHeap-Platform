using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Binders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NewHeap.Platform.AspNet.Common.Services;
using NewHeap.Platform.Common.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NewHeap.Platform.AspNet.Common.Models;
public partial class JsonQueryModelBinder : IModelBinder
{
    private static JsonSerializer? _jsonSerializer;
    private readonly ModelBinderProviderContext _providerContext;

    public JsonQueryModelBinder(
        ModelBinderProviderContext providerContext
        )
    {
        _providerContext = providerContext ?? throw new ArgumentNullException(nameof(providerContext));
    }

    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        try
        {
            var services = bindingContext
                .HttpContext
                .RequestServices;

            var mvcJsonOptions = services.GetRequiredService<IOptions<MvcNewtonsoftJsonOptions>>();
            var mvcOptions = services.GetRequiredService<IOptions<MvcOptions>>();
            var httpCollectionProcessingService = services.GetRequiredService<IHttpCollectionProcessingService>();
            var modelComplexBinderProvider = mvcOptions.Value.ModelBinderProviders.First(p => p.GetType() == typeof(ComplexObjectModelBinderProvider));

            _jsonSerializer ??= JsonSerializer.Create(mvcJsonOptions.Value.SerializerSettings);

            var binder = modelComplexBinderProvider.GetBinder(_providerContext);

            if(binder == null)
            {
                throw new InvalidOperationException("Could not find a suitable binder for the model type.");
            }

            await binder.BindModelAsync(bindingContext);

            if (!bindingContext.Result.IsModelSet)
            {
                return;
            }

            var binderModel = bindingContext.Result.Model as ICollectionRequestModel;
            if (binderModel is null)
            {
                return;
            }

            var requestModel = httpCollectionProcessingService.GetCollectionRequestModel();
            if (typeof(IBaseCollectionRequestModel).IsAssignableFrom(_providerContext.Metadata.ModelType))
            {
                binderModel.ItemsPerPage = requestModel.ItemsPerPage;
                binderModel.Page = requestModel.Page;
            } 
            
            if (typeof(ISearchableBaseCollectionRequestModel).IsAssignableFrom(_providerContext.Metadata.ModelType))
            {
                binderModel.Search = requestModel.Search;
            }

            if (typeof(ICollectionRequestModel).IsAssignableFrom(_providerContext.Metadata.ModelType))
            {

                binderModel.OrderBy = requestModel.OrderBy ?? new List<OrderByCollectionRequestModel>();
                binderModel.Filter = requestModel.Filter ?? new List<FilterCollectionRequestModel>();
            }
        }
        finally
        {
        }
    }

    public class JsonQueryModelBinderProvider : IModelBinderProvider
    {

        public JsonQueryModelBinderProvider()
        {
        }

        public IModelBinder GetBinder(ModelBinderProviderContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var source = context.BindingInfo?.BindingSource;
            if (source != BindingSource.Query)
                return null;

            var typeToCheck = context.Metadata.ModelType;
            if (!typeof(IBaseCollectionRequestModel).IsAssignableFrom(typeToCheck))
            {
                return null;
            }

            return new JsonQueryModelBinder(providerContext: context);
        }
    }
}
