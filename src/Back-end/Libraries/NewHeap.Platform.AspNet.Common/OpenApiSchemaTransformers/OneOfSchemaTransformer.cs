using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using OneOf;

namespace NewHeap.Platform.AspNet.Common.OpenApiSchemaTransformers;
public class OneOfSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
       OpenApiSchema schema,
       OpenApiSchemaTransformerContext context,
       CancellationToken cancellationToken
       )
    {
        var type = context.JsonTypeInfo.Type;

        if (type == null)
        {
            return Task.CompletedTask;
        }

        if (!type.GetInterfaces().Contains(typeof(IOneOf)))
        {
            return Task.CompletedTask;
        }

        Type[] oneOfPossibleTypes = type.GetGenericArguments();

        if (oneOfPossibleTypes.Length < 1 && type.BaseType != null)
        {
            var loopType = type.BaseType;
            while (loopType?.GetInterfaces()?.Contains(typeof(IOneOf)) == true)
            {
                oneOfPossibleTypes = loopType.GetGenericArguments();
                if (oneOfPossibleTypes.Length > 0)
                {
                    break;
                }
                else
                {
                    loopType = loopType.BaseType;
                }
            }
        }

        // Bouw een oneOf-array op met de schema's van de generic types
        //var oneOfSchemas = new List<OpenApiSchema>();
        //foreach (var oneOfType in oneOfPossibleTypes)
        //{
        //    var oneOf = new OpenApiSchema()
        //    {
        //        Type = "object",

        //    };

        //    // Miss kijken of we in document de components kunnen toevoegen.
        //    //var oneOf2 = new OpenApiSchema()
        //    //{
        //    //    Type = JsonSchemaType.Object
        //    //    Reference = new OpenApiReference()
        //    //    {
        //    //        Type = ReferenceType.Schema,
        //    //        Id = oneOfType.Name
        //    //    }
        //    //};

        //    var schemaAsJsonObject = JsonSchemaExporter.GetJsonSchemaAsNode(new System.Text.Json.JsonSerializerOptions()
        //    {
        //        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
        //    }, oneOfType, null);


        //    var deserializedSchema = JsonSerializer.Deserialize<OpenApi(schemaAsJsonObject, _jsonSchemaContext.OpenApiJsonSchema);

        //    schemaObject.
        //    oneOfSchemas.Add(schemaObject);
        //    oneOfSchemas.Add(oneOf);
        //    schema.OneOf.Add(oneOf);
        //}

        // Verwijder overige properties om verwarring te voorkomen.
        //https://github.com/dotnet/aspnetcore/issues/57798
        schema.Properties?.Clear();
        schema.Type = JsonSchemaType.Object;

        return Task.CompletedTask;
    }
}
