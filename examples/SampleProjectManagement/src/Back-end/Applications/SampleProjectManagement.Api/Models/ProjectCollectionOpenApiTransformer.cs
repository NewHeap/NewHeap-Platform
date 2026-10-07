using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using NewHeap.Platform.AspNet.Common.Models;
using NewHeap.Platform.AspNet.Common.Models.ResponseTypes;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Core.Models.View;

namespace SampleProjectManagement.Api.Models;

/// <summary>Documents the opt-in response without changing the legacy runtime contract.</summary>
public sealed class ProjectCollectionOpenApiTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.Description.HttpMethod != "GET" || context.Description.RelativePath != "projects")
        {
            return;
        }

        var legacy = await context.GetOrCreateSchemaAsync(typeof(CollectionResultModel<ProjectViewModel>), cancellationToken: cancellationToken);
        var selected = await context.GetOrCreateSchemaAsync(typeof(NhSelectedCollectionResult), cancellationToken: cancellationToken);
        var validation = await context.GetOrCreateSchemaAsync(typeof(ModelStateResponseType), cancellationToken: cancellationToken);
        var invalidSelection = await context.GetOrCreateSchemaAsync(typeof(TaskResult<NhSelectedCollectionResult>), cancellationToken: cancellationToken);
        var success = operation.Responses!["200"];
        success.Content!["application/json"].Schema = new OpenApiSchema { OneOf = [legacy, selected] };
        success.Content["application/x-msgpack"] = new OpenApiMediaType { Schema = selected };
        operation.Responses["400"].Content!["application/json"].Schema = new OpenApiSchema { OneOf = [validation, invalidSelection] };
    }
}
