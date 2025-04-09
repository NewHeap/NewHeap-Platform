using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace NewHeap.Platform.AspNet.Common.SchemaFilters;
public class OneOfSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken
        )
    {   
        // TODO: testing inopg
        return Task.CompletedTask;
    }
}
