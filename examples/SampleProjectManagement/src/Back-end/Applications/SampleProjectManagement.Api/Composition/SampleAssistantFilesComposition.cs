using Microsoft.Extensions.DependencyInjection.Extensions;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.AI.Chat.AspNet;
using SampleProjectManagement.Api.Services;
using SampleProjectManagement.Core.Services;

namespace SampleProjectManagement.Api.Composition;

/// <summary>
/// SPM-305: lets assistant turns read files of the sample's media library. People attach PDFs,
/// images, Word and Excel documents to a chat message; tools name project documents in their
/// results. <see cref="ProjectMediaFileSource"/> stores uploads in the media library and opens every
/// file for the actor of the turn, the chat profile's fallback permits the <c>Confidential</c>
/// classification uploads get, the model reads PDFs and images itself and
/// <see cref="OfficeDocumentReader"/> turns Word and Excel documents into text. Every upload passes
/// <see cref="EicarUploadScanner"/> before it is stored. Call it after <c>AddSampleAssistant</c> and
/// the media library.
/// </summary>
public static class SampleAssistantFilesComposition
{
    public static IServiceCollection AddSampleAssistantFiles(this IServiceCollection services)
    {
        // SPM-306: the tool that names a project's documents for agents that accept files.
        services.TryAddScoped<IProjectAiDocumentService, ProjectMediaDocuments>();
        services.TryAddScoped<ProjectDocumentAiTools>();
        services.AddNewHeapPlatformAI(ai => ai.AddGeneratedToolCatalog<ProjectDocumentAiToolsNhAiCatalog>());

        services.AddNewHeapAssistant(assistant => assistant
            // Without a file source turns take no files. The chat assistant accepts them at once; an
            // agent with run settings and its skills only after AcceptsFiles().
            .UseFileSource<ProjectMediaFileSource>()
            .ConfigureUploads(uploads =>
            {
                uploads.AcceptMediaTypes(
                    NhAiMediaTypes.Pdf,
                    NhAiMediaTypes.Png,
                    NhAiMediaTypes.Jpeg,
                    NhAiMediaTypes.Docx,
                    NhAiMediaTypes.Xlsx);
                // The default, stated for readers of the sample: the profile's fallback permits it.
                uploads.DefaultClassification = NhAiDataClassification.Confidential;
                // Every upload passes the example scanner before the media library stores it.
                uploads.UseScanner<EicarUploadScanner>();
            })
            // The model reads PDFs and images itself; Word and Excel documents reach it as text. The
            // reader runs in this process, so it may receive confidential documents.
            .AddFileReader<OfficeDocumentReader>(OfficeDocumentReader.ReaderId, reader => reader
                .AcceptMediaTypes(NhAiMediaTypes.Docx, NhAiMediaTypes.Xlsx)
                .PermitDataClassifications(NhAiDataClassification.Internal, NhAiDataClassification.Confidential)));
        return services;
    }
}
