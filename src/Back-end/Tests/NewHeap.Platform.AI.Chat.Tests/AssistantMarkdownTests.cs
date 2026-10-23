using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

/// <summary>
/// <see cref="NhAssistantMarkdown.Escape"/> lets the application put data people entered into the
/// Markdown of an application message: the chat shows it literally, with its line breaks.
/// </summary>
public sealed class AssistantMarkdownTests
{
    /// <summary>
    /// A project name that tries every kind of Markdown. The chat's
    /// <c>nh-assistant-markdown.spec.ts</c> renders <see cref="EscapedProjectName"/> and shows exactly
    /// this text, without formatting, links or HTML.
    /// </summary>
    public const string ProjectName = "# **Launch** <b>now</b> [site](https://evil.example) _v2_ ~~old~~ | www.example.com & co";

    public const string EscapedProjectName = @"\# \*\*Launch\*\* \<b\>now\<\/b\> \[site\]\(https\:\/\/evil\.example\) \_v2\_ \~\~old\~\~ \| www\.example\.com \& co";

    [Fact]
    public void Every_kind_of_markdown_in_people_s_data_is_escaped()
    {
        Assert.Equal(EscapedProjectName, NhAssistantMarkdown.Escape(ProjectName));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("Plain text 2026", "Plain text 2026")]
    [InlineData("Café ✓ «quoted»", "Café ✓ «quoted»")]
    [InlineData(@"C:\temp\*", @"C\:\\temp\\\*")]
    [InlineData("1. First\n2) Second\n+ third\n> quote", "1\\. First\n2\\) Second\n\\+ third\n\\> quote")]
    [InlineData("Title\n===\n---", "Title\n\\=\\=\\=\n\\-\\-\\-")]
    [InlineData("!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~", "\\!\\\"\\#\\$\\%\\&\\'\\(\\)\\*\\+\\,\\-\\.\\/\\:\\;\\<\\=\\>\\?\\@\\[\\\\\\]\\^\\_\\`\\{\\|\\}\\~")]
    public void Ascii_punctuation_gets_a_backslash_and_other_text_stays_as_it_is(string text, string expected)
    {
        Assert.Equal(expected, NhAssistantMarkdown.Escape(text));
    }

    [Fact]
    public void Line_breaks_stay_and_indentation_that_would_start_a_code_block_is_left_out()
    {
        var escaped = NhAssistantMarkdown.Escape("Line one\r\n    indented\rLine three\n\n\tNew paragraph  ");

        Assert.Equal("Line one\nindented\nLine three\n\nNew paragraph  ", escaped);
    }

    [Fact]
    public void A_missing_text_is_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(() => NhAssistantMarkdown.Escape(null!));
    }
}
