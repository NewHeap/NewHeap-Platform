using Microsoft.AspNetCore.Http;
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
    private readonly IModelBinder _defaultModelBinder;

    public JsonQueryModelBinder(
        IModelBinder defaultModelBinder
        )
    {
        _defaultModelBinder = defaultModelBinder;
    }

    public async Task BindModelAsync(ModelBindingContext bindingContext)
    {
        try
        {
            var services = bindingContext
                .HttpContext
                .RequestServices;
            var httpCollectionProcessingService = services.GetRequiredService<IHttpCollectionProcessingService>();

            if(_defaultModelBinder == null)
            {
                throw new InvalidOperationException("Could not find a suitable binder for the model type.");
            }

            await _defaultModelBinder.BindModelAsync(bindingContext);

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
            if (typeof(IBaseCollectionRequestModel).IsAssignableFrom(bindingContext.ModelType))
            {
                binderModel.ItemsPerPage = requestModel.ItemsPerPage;
                binderModel.Page = requestModel.Page;
            } 
            
            if (typeof(ISearchableBaseCollectionRequestModel).IsAssignableFrom(bindingContext.ModelType))
            {
                binderModel.Search = requestModel.Search;
            }

            if (typeof(ICollectionRequestModel).IsAssignableFrom(bindingContext.ModelType))
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
        private static readonly object BinderKey = new object();

        public JsonQueryModelBinderProvider()
        {
        }

        public IModelBinder? GetBinder(ModelBinderProviderContext context)
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

            var httpContextAccessor = context.Services.GetService<IHttpContextAccessor>();
            var httpContext = httpContextAccessor?.HttpContext;
            if (httpContext != null)
            {
                if (httpContext.Items.ContainsKey(BinderKey))
                {
                    return null;
                }
                httpContext.Items[BinderKey] = true;
            }

            var defaultBinder = context.CreateBinder(context.Metadata);

            return new JsonQueryModelBinder(defaultBinder);
        }
    }
}
