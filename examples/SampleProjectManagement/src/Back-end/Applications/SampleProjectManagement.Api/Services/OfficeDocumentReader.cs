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
/// calculated value, each worksheet once however many sheets name it.
/// </summary>
/// <remarks>
/// Two bounds keep a small file from making a large text. <see cref="MaxCharactersInPart"/> bounds
/// what one part of the package unpacks to; it bounds no total, because cells can name the same
/// shared string again and again. The text itself is bounded while it is made, cell by cell: at
/// most the turn's budget for it (<see cref="NhAssistantFileReadRequest.MaxTextCharacters"/>), which
/// refuses the file with <c>assistant-file-token-limit</c>, and never more than
/// <see cref="MaxCharactersInText"/>. A part above its bound, a text above the reader's own bound
/// and a damaged package fail the file with <c>office-document-unreadable</c>, which the assistant
/// refuses with <c>assistant-file-reader-failed</c>; nothing is cut off silently. The reader checks
/// the turn's cancellation at every row and paragraph.
/// </remarks>
public sealed class OfficeDocumentReader : INhAssistantFileReader
{
    public const string ReaderId = "office-documents";

    /// <summary>
    /// The most characters one part of a package may unpack to, which bounds a part that compresses
    /// well far beyond its file size.
    /// </summary>
    public const long MaxCharactersInPart = 10_000_000;

    /// <summary>
    /// The most characters of text the reader makes of one document, whatever the turn's budget: the
    /// most text a turn can take, 2,000,000 tokens of four characters.
    /// </summary>
    public const long MaxCharactersInText = 8_000_000;

    public const string UnreadableCode = "office-document-unreadable";

    private static readonly OpenSettings Settings = new() { MaxCharactersInPart = MaxCharactersInPart };

    public Task<TaskResult<string>> ReadAsync(NhAssistantFileReadRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = new DocumentText(Math.Min(request.MaxTextCharacters, MaxCharactersInText), cancellationToken);
        using var content = new MemoryStream(request.Content.ToArray(), writable: false);
        try
        {
            var read = request.MediaType switch
            {
                NhAiMediaTypes.Docx => ReadWordDocument(content, text),
                NhAiMediaTypes.Xlsx => ReadWorkbook(content, text),
                _ => false
            };
            return Task.FromResult(read
                ? TaskResult<string>.Succeeded(text.ToString())
                : TaskResult<string>.Failed(UnreadableCode, "The reader does not read this media type."));
        }
        catch (DocumentTextTooLongException) when (request.MaxTextCharacters <= MaxCharactersInText)
        {
            // The turn could not take this text: the assistant refuses the file with its token limit.
            return Task.FromResult(TaskResult<string>.Failed(NhAssistantErrorCodes.FileTokenLimit, "The document's text is longer than the turn can take."));
        }
        catch (Exception exception) when (exception is DocumentTextTooLongException or OpenXmlPackageException or InvalidDataException or IOException or FormatException)
        {
            // A damaged or oversized package. The message may quote the document, so it stays here.
            return Task.FromResult(TaskResult<string>.Failed(UnreadableCode, "The document could not be read."));
        }
    }

    private static bool ReadWordDocument(Stream content, DocumentText text)
    {
        using var document = WordprocessingDocument.Open(content, false, Settings);
        var body = document.MainDocumentPart?.Document?.Body;
        foreach (var element in body?.Elements() ?? [])
        {
            if (element is W.Paragraph paragraph)
            {
                text.StartLine();
                text.Append(paragraph.InnerText);
            }
            else if (element is W.Table table)
            {
                foreach (var row in table.Elements<W.TableRow>())
                {
                    text.StartLine();
                    AppendCells(text, row.Elements<W.TableCell>().Select(cell => cell.InnerText));
                }
            }
        }

        return true;
    }

    private static bool ReadWorkbook(Stream content, DocumentText text)
    {
        using var document = SpreadsheetDocument.Open(content, false, Settings);
        var workbook = document.WorkbookPart;
        if (workbook?.Workbook?.Sheets is not { } sheets)
        {
            return true;
        }

        var sharedStrings = workbook.SharedStringTablePart?.SharedStringTable?
            .Elements<S.SharedStringItem>()
            .Select(item => item.InnerText)
            .ToArray() ?? [];
        // A workbook can name one worksheet from several sheets; each worksheet is read once.
        var read = new HashSet<Uri>();
        foreach (var sheet in sheets.Elements<S.Sheet>())
        {
            if (sheet.Id?.Value is not { } partId
                || workbook.GetPartById(partId) is not WorksheetPart part
                || !read.Add(part.Uri))
            {
                continue;
            }

            text.StartLine();
            text.Append($"Sheet: {sheet.Name?.Value}");
            foreach (var row in part.Worksheet?.Descendants<S.Row>() ?? [])
            {
                text.StartLine();
                AppendCells(text, row.Elements<S.Cell>().Select(cell => ValueOf(cell, sharedStrings)));
            }
        }

        return true;
    }

    /// <summary>
    /// Appends the values of one row, separated by <c> | </c>, each within the text's bound.
    /// </summary>
    private static void AppendCells(DocumentText text, IEnumerable<string> values)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                text.Append(" | ");
            }
            text.Append(value);
            first = false;
        }
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

    /// <summary>
    /// The text of a document as it is made: lines of at most <c>maxCharacters</c> together, checked
    /// before every append, so a text above the bound is never built. Every new line checks the turn's
    /// cancellation.
    /// </summary>
    private sealed class DocumentText(long maxCharacters, CancellationToken cancellationToken)
    {
        private readonly StringBuilder _text = new();

        public void StartLine()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_text.Length > 0)
            {
                Append("\n");
            }
        }

        public void Append(string value)
        {
            if (_text.Length + (long)value.Length > maxCharacters)
            {
                throw new DocumentTextTooLongException();
            }
            _text.Append(value);
        }

        public override string ToString()
        {
            return _text.ToString().TrimEnd();
        }
    }

    /// <summary>The document's text would be longer than its bound.</summary>
    private sealed class DocumentTextTooLongException : Exception
    {
    }
}
