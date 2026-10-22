using System.Text;
using NewHeap.Platform.AI.Chat;
using NewHeap.Platform.Common.Models;

namespace SampleProjectManagement.Api.Services;

/// <summary>
/// SPM-305: an example upload scanner, registered with
/// <c>ConfigureUploads(uploads =&gt; uploads.UseScanner&lt;EicarUploadScanner&gt;())</c>. The assistant
/// calls it for every upload after the checks of the type, the size and the content's signature and
/// before the media library stores anything. It refuses content that contains the EICAR anti-virus
/// test string, which the assistant answers with <c>assistant-file-scan-refused</c>. A real application
/// calls its virus scanner here and returns <see cref="NhAssistantErrorCodes.FileUploadFailed"/> when
/// the scanner cannot be reached, so the person may try again.
/// </summary>
public sealed class EicarUploadScanner : INhAssistantUploadScanner
{
    /// <summary>
    /// The code of a refusal; the assistant shows the person its own code and never this message.
    /// </summary>
    public const string MalwareFoundCode = "sample-upload-malware-found";

    /// <summary>
    /// The EICAR test string, assembled at runtime so no source file contains it whole and a virus
    /// scanner on a developer's machine leaves the repository alone.
    /// </summary>
    internal static readonly byte[] TestSignature = Encoding.ASCII.GetBytes(
        string.Concat("X5O!P%@AP[4\\PZX54(P^)7CC)7}$", "EICAR-STANDARD-", "ANTIVIRUS-TEST-FILE!$H+H*"));

    public async Task<TaskResult> ScanAsync(
        NhAssistantFileUpload upload,
        NhAssistantFileActor actor,
        CancellationToken cancellationToken)
    {
        // The assistant bounds an upload to the agent's largest file, so the sample reads it whole.
        using var content = new MemoryStream();
        await upload.Content.CopyToAsync(content, cancellationToken);
        return content.GetBuffer().AsSpan(0, (int)content.Length).IndexOf(TestSignature) >= 0
            ? TaskResult.Failed(MalwareFoundCode, "The upload contains the EICAR test file.")
            : TaskResult.Succeeded();
    }
}
