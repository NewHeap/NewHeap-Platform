using System.Text;

namespace NewHeap.Platform.AI.Chat;

/// <summary>
/// Markdown helpers for the text the application writes into the chat, such as an application
/// message (<see cref="NhAssistantApplicationMessage"/>), which the chat renders as Markdown.
/// </summary>
public static class NhAssistantMarkdown
{
    /// <summary>
    /// The ASCII punctuation characters that Markdown may give a meaning and that a backslash shows as
    /// they are: the backslash-escapable characters of CommonMark.
    /// </summary>
    private const string EscapableCharacters = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    /// <summary>
    /// Returns <paramref name="text"/> so that the chat shows it literally inside Markdown, for data
    /// people entered, such as a project name: every ASCII punctuation character that Markdown may give
    /// a meaning gets a backslash, so it starts no emphasis, link, list, heading, table, HTML or
    /// automatic link. Spaces and tabs at the start of a line are left out, because they would start a
    /// code block and the chat does not show them anyway. Line breaks stay line breaks (<c>\r\n</c> and
    /// <c>\r</c> become <c>\n</c>) and a blank line still separates paragraphs.
    /// <para>
    /// Use the result in running text, a heading or the text of a link, not inside a code span, a code
    /// block or the address of a link, where Markdown shows a backslash as it is. Escaping changes how
    /// text looks, not what it says: never put the result into a
    /// <see cref="NhAssistantApplicationMessage.Trusted"/> message.
    /// </para>
    /// </summary>
    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length + 16);
        var lineStart = true;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                builder.Append('\n');
                lineStart = true;
                continue;
            }
            if (lineStart && (character is ' ' or '\t'))
            {
                continue;
            }

            lineStart = false;
            if (EscapableCharacters.Contains(character, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }
            builder.Append(character);
        }

        return builder.ToString();
    }
}
