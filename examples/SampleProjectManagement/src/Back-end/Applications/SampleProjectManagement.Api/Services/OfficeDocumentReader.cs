using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using NewHeap.Platform.AI;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.Common.Models;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-305: an example reading tool that turns Word and Excel documents into text, for models that
/// read PDFs and images but not Office documents. Copy it and adapt it: NewHeap ships no document
/// library in its packages. The assistant gives the model the text behind its external-data marker;
/// the reader never decides what the model may do with it. It reads the paragraphs and table rows of
/// a Word document and the cell values of every sheet of a workbook, formulas as their last
/// calculated value. A part above <see cref="MaxCharactersInPart"/> or a damaged package fails the
/// file, which the assistant refuses with <c>assistant-file-reader-failed</c>; nothing is cut off
/// silently, and the turn's file limits count the text.
/// </summary>
public sealed class OfficeDocumentReader : INhAssistantFileReader
{
    public const string ReaderId = "office-documents";

    /// <summary>
    /// The most characters one part of a package may unpack to, which bounds a document that
    /// compresses well far beyond its file size.
    /// </summary>
    public const long MaxCharactersInPart = 10_000_000;

    public const string UnreadableCode = "office-document-unreadable";

    private static readonly OpenSettings Settings = new() { MaxCharactersInPart = MaxCharactersInPart };

    public Task<TaskResult<string>> ReadAsync(NhAssistantFileReadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var content = new MemoryStream(request.Content.ToArray(), writable: false);
        try
        {
            var text = request.MediaType switch
            {
                NhAiMediaTypes.Docx => ReadWordDocument(content),
                NhAiMediaTypes.Xlsx => ReadWorkbook(content),
                _ => null
            };
            return Task.FromResult(text is null
                ? TaskResult<string>.Failed(UnreadableCode, "The reader does not read this media type.")
                : TaskResult<string>.Succeeded(text));
        }
        catch (Exception exception) when (exception is OpenXmlPackageException or InvalidDataException or IOException or FormatException)
        {
            // A damaged or oversized package. The message may quote the document, so it stays here.
            return Task.FromResult(TaskResult<string>.Failed(UnreadableCode, "The document could not be read."));
        }
    }

    private static string ReadWordDocument(Stream content)
    {
        using var document = WordprocessingDocument.Open(content, false, Settings);
        var text = new StringBuilder();
        var body = document.MainDocumentPart?.Document?.Body;
        foreach (var element in body?.Elements() ?? [])
        {
            if (element is W.Paragraph paragraph)
            {
                text.AppendLine(paragraph.InnerText);
            }
            else if (element is W.Table table)
            {
                foreach (var row in table.Elements<W.TableRow>())
                {
                    text.AppendLine(string.Join(" | ", row.Elements<W.TableCell>().Select(cell => cell.InnerText)));
                }
            }
        }

        return text.ToString().TrimEnd();
    }

    private static string ReadWorkbook(Stream content)
    {
        using var document = SpreadsheetDocument.Open(content, false, Settings);
        var workbook = document.WorkbookPart;
        if (workbook?.Workbook?.Sheets is not { } sheets)
        {
            return string.Empty;
        }

        var sharedStrings = workbook.SharedStringTablePart?.SharedStringTable?
            .Elements<S.SharedStringItem>()
            .Select(item => item.InnerText)
            .ToArray() ?? [];
        var text = new StringBuilder();
        foreach (var sheet in sheets.Elements<S.Sheet>())
        {
            if (sheet.Id?.Value is not { } partId || workbook.GetPartById(partId) is not WorksheetPart part)
            {
                continue;
            }

            text.AppendLine($"Sheet: {sheet.Name?.Value}");
            foreach (var row in part.Worksheet?.Descendants<S.Row>() ?? [])
            {
                text.AppendLine(string.Join(" | ", row.Elements<S.Cell>().Select(cell => ValueOf(cell, sharedStrings))));
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The value a cell shows: a shared or inline text, or the stored value of a number, a date or a
    /// formula's last result.
    /// </summary>
    private static string ValueOf(S.Cell cell, string[] sharedStrings)
    {
        var value = cell.CellValue?.Text ?? string.Empty;
        if (cell.DataType?.Value == S.CellValues.SharedString)
        {
            return int.TryParse(value, out var index) && index >= 0 && index < sharedStrings.Length
                ? sharedStrings[index]
                : string.Empty;
        }
        if (cell.DataType?.Value == S.CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        return value;
    }
}
