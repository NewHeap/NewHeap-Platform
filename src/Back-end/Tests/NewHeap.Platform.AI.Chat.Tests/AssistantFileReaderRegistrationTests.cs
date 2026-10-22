using Microsoft.Extensions.DependencyInjection;
using NewHeap.Platform.AI.Chat.AspNet;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// The declaration of a reading tool with <c>AddFileReader&lt;T&gt;()</c>: an id, the media types it
/// accepts, lowercase and without parameters, and the classifications it may receive. Invalid
/// declarations stop the composition.
/// </summary>
public sealed class AssistantFileReaderRegistrationTests
{
    [Fact]
    public void A_reader_declares_its_media_types_and_the_classifications_it_may_receive()
    {
        var state = Register(assistant => assistant
            .AddFileReader<TestSpreadsheetReader>("spreadsheets", reader => reader
                .AcceptMediaTypes("Application/VND.openxmlformats-officedocument.spreadsheetml.sheet; charset=binary")
                .PermitDataClassifications(NhAiDataClassification.Internal))
            .AddFileReader<TestSpreadsheetReader>("public-sheets", reader => reader.AcceptMediaTypes(NhAiMediaTypes.Xlsx)));

        Assert.Equal(["spreadsheets", "public-sheets"], state.FileReaders.Select(reader => reader.Id).ToArray());
        var spreadsheets = state.FileReaders[0];
        Assert.Equal([NhAiMediaTypes.Xlsx], spreadsheets.MediaTypes.ToArray());
        Assert.True(spreadsheets.Permits(NhAiDataClassification.Internal));
        // Public is always permitted, as for a model profile; nothing more without a declaration.
        Assert.Equal([NhAiDataClassification.Public], state.FileReaders[1].Classifications.ToArray());
        Assert.False(state.FileReaders[1].Permits(NhAiDataClassification.Confidential));
    }

    [Fact]
    public void An_invalid_reader_declaration_stops_the_composition()
    {
        Assert.Throws<ArgumentException>(() => Register(assistant => assistant
            .AddFileReader<TestSpreadsheetReader>("Spreadsheets", reader => reader.AcceptMediaTypes(NhAiMediaTypes.Xlsx))));
        Assert.Throws<ArgumentException>(() => Register(assistant => assistant
            .AddFileReader<TestSpreadsheetReader>("spreadsheets", reader => reader.AcceptMediaTypes("spreadsheet"))));
        Assert.Throws<InvalidOperationException>(() => Register(assistant => assistant
            .AddFileReader<TestSpreadsheetReader>("spreadsheets", _ => { })));
        Assert.Throws<ArgumentOutOfRangeException>(() => Register(assistant => assistant
            .AddFileReader<TestSpreadsheetReader>("spreadsheets", reader => reader
                .AcceptMediaTypes(NhAiMediaTypes.Xlsx)
                .PermitDataClassifications((NhAiDataClassification)9))));
        var duplicate = Assert.Throws<InvalidOperationException>(() => Register(assistant => assistant
            .AddFileReader<TestSpreadsheetReader>("spreadsheets", reader => reader.AcceptMediaTypes(NhAiMediaTypes.Xlsx))
            .AddFileReader<TestSpreadsheetReader>("spreadsheets", reader => reader.AcceptMediaTypes(NhAiMediaTypes.Docx))));
        Assert.Contains("spreadsheets", duplicate.Message, StringComparison.Ordinal);
    }

    private static NhAssistantRegistrationState Register(Action<NhAssistantBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddNewHeapAssistant(configure);
        return (NhAssistantRegistrationState)services
            .Single(descriptor => descriptor.ServiceType == typeof(NhAssistantRegistrationState))
            .ImplementationInstance!;
    }
}
