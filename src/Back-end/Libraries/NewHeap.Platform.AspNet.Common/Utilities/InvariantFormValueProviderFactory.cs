using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Globalization;

namespace NewHeap.Platform.AspNet.Common.Utilities;

public sealed class InvariantFormValueProviderFactory : IValueProviderFactory
{
    public async Task CreateValueProviderAsync(ValueProviderFactoryContext context)
    {
        var request = context.ActionContext.HttpContext.Request;
        
        if (!request.HasFormContentType)
            return;

        var form = await request.ReadFormAsync();

        var valueProvider = new FormValueProvider(
            BindingSource.Form,
            form,
            CultureInfo.InvariantCulture
        );

        context.ValueProviders.Add(valueProvider);
    }
}
