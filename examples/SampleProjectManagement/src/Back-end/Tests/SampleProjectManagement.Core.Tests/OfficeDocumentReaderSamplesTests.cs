using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.Common.Models;
using SampleProjectManagement.Api.Services;
using Xunit;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SampleProjectManagement.Core.Tests;

/// <summary>
/// SPM-305: the example reader bounds the text it makes, not only what one part of a document unpacks
/// to. A few kilobytes of workbook whose cells repeat one long shared string, or a worksheet that
/// several sheets name, never become a text longer than the turn can take: the reader stops at the
/// turn's budget with the token limit, reads every worksheet once and stops when the turn is
/// cancelled, while an ordinary document still reads.
/// </summary>
public sealed class OfficeDocumentReaderSamplesTests
{
    private readonly OfficeDocumentReader _reader = new();

    [Fact]
    public async Task SPM_305_a_workbook_whose_cells_repeat_one_long_shared_string_stops_at_the_turns_budget()
    {
        // About ten million characters of text from a workbook of a few kilobytes.
        var workbook = RepeatedSharedStringWorkbook(rows: 1_000, sharedStringLength: 10_000);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        var read = await _reader.ReadAsync(Request(NhAiMediaTypes.Xlsx, workbook, maxTextCharacters: 400_000), TestContext.Current.CancellationToken);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(NhAssistantErrorCodes.FileTokenLimit, Code(read));
        // The reader stopped at the budget instead of building the whole text first.
        Assert.True(allocated < 32 * 1024 * 1024, $"The reader allocated {allocated:N0} bytes.");
    }

    [Fact]
    public async Task SPM_305_a_text_beyond_the_readers_own_bound_fails_the_document()
    {
        var workbook = RepeatedSharedStringWorkbook(rows: 900, sharedStringLength: 10_000);

        // Without a budget of the turn the reader's own bound applies.
        var read = await _reader.ReadAsync(Request(NhAiMediaTypes.Xlsx, workbook, long.MaxValue), TestContext.Current.CancellationToken);

        Assert.Equal(OfficeDocumentReader.UnreadableCode, Code(read));
    }

    [Fact]
    public async Task SPM_305_a_worksheet_that_several_sheets_name_is_read_once()
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            var part = workbook.AddNewPart<WorksheetPart>();
            part.Worksheet = new S.Worksheet(new S.SheetData(
                new S.Row(InlineCell("Item"), InlineCell("Amount")),
                new S.Row(InlineCell("Hardware"), new S.Cell { DataType = S.CellValues.Number, CellValue = new S.CellValue("1200") })));
            var id = workbook.GetIdOfPart(part);
            workbook.Workbook.AppendChild(new S.Sheets(Enumerable.Range(1, 20).Select(index => new S.Sheet
            {
                Id = id,
                SheetId = (uint)index,
                Name = "Budget " + index
            })));
        }

        var read = await _reader.ReadAsync(Request(NhAiMediaTypes.Xlsx, stream.ToArray(), 100_000), TestContext.Current.CancellationToken);

        Assert.True(read.Success);
        Assert.Equal("Sheet: Budget 1\nItem | Amount\nHardware | 1200", read.Data);
    }

    [Fact]
    public async Task SPM_305_a_word_document_is_bounded_by_the_turns_budget_too()
    {
        var document = WordDocument(Enumerable.Range(1, 100).Select(index => $"Paragraph {index}: " + new string('w', 1_000)).ToArray());

        var tooLong = await _reader.ReadAsync(Request(NhAiMediaTypes.Docx, document, 50_000), TestContext.Current.CancellationToken);
        var fits = await _reader.ReadAsync(Request(NhAiMediaTypes.Docx, document, 200_000), TestContext.Current.CancellationToken);

        Assert.Equal(NhAssistantErrorCodes.FileTokenLimit, Code(tooLong));
        Assert.True(fits.Success);
        Assert.Equal(100, fits.Data!.Split('\n').Length);
    }

    [Fact]
    public async Task SPM_305_the_reader_stops_when_the_turn_is_cancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var workbook = RepeatedSharedStringWorkbook(rows: 10, sharedStringLength: 10);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _reader.ReadAsync(Request(NhAiMediaTypes.Xlsx, workbook, 100_000), cancelled.Token));
    }

    private static NhAssistantFileReadRequest Request(string mediaType, byte[] content, long maxTextCharacters)
    {
        return new NhAssistantFileReadRequest(
            new NhAiFileReference("document-1", mediaType == NhAiMediaTypes.Xlsx ? "Budget.xlsx" : "Brief.docx", mediaType, content.Length),
            mediaType,
            content,
            NhAiDataClassification.Internal,
            new NhAssistantFileActor("person-1", NhAiActorKind.Human, "project-assistant", Guid.NewGuid()))
        {
            MaxTextCharacters = maxTextCharacters
        };
    }

    /// <summary>
    /// A workbook of one sheet whose every row has one cell that names the same long shared string.
    /// </summary>
    private static byte[] RepeatedSharedStringWorkbook(int rows, int sharedStringLength)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new S.Workbook();
            var strings = workbook.AddNewPart<SharedStringTablePart>();
            strings.SharedStringTable = new S.SharedStringTable(new S.SharedStringItem(new S.Text(new string('s', sharedStringLength))));
            var part = workbook.AddNewPart<WorksheetPart>();
            part.Worksheet = new S.Worksheet(new S.SheetData(Enumerable.Range(1, rows).Select(_ =>
                new S.Row(new S.Cell { DataType = S.CellValues.SharedString, CellValue = new S.CellValue("0") }))));
            workbook.Workbook.AppendChild(new S.Sheets(new S.Sheet
            {
                Id = workbook.GetIdOfPart(part),
                SheetId = 1,
                Name = "Repeated"
            }));
        }

        return stream.ToArray();
    }

    private static byte[] WordDocument(params string[] paragraphs)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body(
                paragraphs.Select(text => new W.Paragraph(new W.Run(new W.Text(text))))));
        }

        return stream.ToArray();
    }

    private static S.Cell InlineCell(string value)
    {
        return new S.Cell { DataType = S.CellValues.InlineString, InlineString = new S.InlineString(new S.Text(value)) };
    }

    private static string? Code(TaskResult<string> result)
    {
        Assert.False(result.Success);
        return result.GetResultItems().Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }
}
