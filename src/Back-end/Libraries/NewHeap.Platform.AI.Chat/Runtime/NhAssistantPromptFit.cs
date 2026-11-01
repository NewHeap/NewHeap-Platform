namespace NewHeap.Platform.AI.Chat.Runtime;

/// <summary>
/// The one step that fits a turn's text to the limit of its model call, in a fixed order. The
/// lowest-ranked passages go first, one at a time. Only when the text does not fit without any passage
/// does the application map go, and the room it leaves goes back to the passages. The turn data stays
/// as long as it fits; after that the turn runs on its instructions alone. <see cref="Prompt"/> holds
/// what remains, so its hashes name what the model receives.
/// </summary>
internal sealed record NhAssistantPromptFit(
    NhAssistantComposedPrompt Prompt,
    int KeptPassages,
    int DroppedPassages,
    bool ApplicationMapDropped,
    bool TurnDataDropped)
{
    /// <summary>
    /// The blank line between the blocks of <see cref="NhAssistantComposedPrompt.ModelInstructions"/>.
    /// </summary>
    private const int BlockSeparatorLength = 2;

    /// <summary>
    /// Whether the model receives less than the turn has.
    /// </summary>
    public bool LeftDataOut => DroppedPassages > 0 || ApplicationMapDropped || TurnDataDropped;

    /// <summary>
    /// Fits <paramref name="prompt"/> and <paramref name="retrieved"/> to
    /// <paramref name="maxCharacters"/>. Text that fits stays exactly as it is.
    /// </summary>
    public static NhAssistantPromptFit Of(
        NhAssistantComposedPrompt prompt,
        NhAssistantRetrievedContext? retrieved,
        int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var bare = prompt with { RetrievedContext = string.Empty, RetrievedContextHash = null };
        var withoutMap = bare with { ApplicationMap = string.Empty, ApplicationMapHash = null };
        var hasMap = prompt.ApplicationMap.Length > 0;
        var found = (retrieved?.Passages ?? 0) + (retrieved?.LeftOut ?? 0);

        foreach (var candidate in hasMap ? new[] { bare, withoutMap } : [bare])
        {
            var fitted = retrieved?.FitTo(maxCharacters - candidate.ModelInstructions.Length - BlockSeparatorLength);
            var sent = candidate with
            {
                RetrievedContext = fitted?.Block ?? string.Empty,
                RetrievedContextHash = fitted?.Hash
            };
            if (sent.ModelInstructions.Length <= maxCharacters)
            {
                var kept = fitted?.Passages ?? 0;
                return new NhAssistantPromptFit(sent, kept, found - kept, hasMap && candidate.ApplicationMap.Length == 0, false);
            }
        }

        // Not even the note about the passages fits next to the turn data, which goes last.
        return withoutMap.ModelInstructions.Length <= maxCharacters
            ? new NhAssistantPromptFit(withoutMap, 0, found, hasMap, false)
            : new NhAssistantPromptFit(InstructionsOnly(prompt), 0, found, hasMap, prompt.TurnData.Length > 0);
    }

    /// <summary>
    /// <paramref name="prompt"/> without any turn data, as a turn that cannot fit it runs.
    /// </summary>
    public static NhAssistantComposedPrompt InstructionsOnly(NhAssistantComposedPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return prompt with
        {
            ApplicationMap = string.Empty,
            ApplicationMapHash = null,
            TurnData = string.Empty,
            FactCount = 0,
            PageEntityCount = 0,
            HadPageContext = false,
            RetrievedContext = string.Empty,
            RetrievedContextHash = null
        };
    }
}
