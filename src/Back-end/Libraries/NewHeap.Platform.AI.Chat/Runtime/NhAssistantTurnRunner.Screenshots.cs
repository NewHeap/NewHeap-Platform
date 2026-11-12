using System.Text.Json;
using NewHeap.Platform.AI.Chat.Runs;

namespace NewHeap.Platform.AI.Chat.Runtime;

internal sealed partial class NhAssistantTurnRunner
{
    private static NhAssistantMemoryFunction CreatePageScreenshotFunction(NhAssistantTurnState state)
    {
        return new NhAssistantMemoryFunction(
            NhAssistantMemoryFunction.PageScreenshotDescriptor,
            "Requests a screenshot of the user's current application page when visual context would help. "
                + "Explain why in the user's language. This only opens a consent dialog: it does not capture or upload anything. "
                + "Ask at most once per turn. End your response and wait for the user to share an image in a new message; "
                + "they may decline. Never claim to see the page before receiving its image.",
            """{"type":"object","properties":{"reason":{"type":"string","minLength":1,"maxLength":500,"description":"Why seeing this page would help."}},"required":["reason"],"additionalProperties":false}""",
            async (arguments, _) =>
            {
                var reason = arguments.TryGetValue("reason", out var value) ? value switch
                {
                    string text => text.Trim(),
                    JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()?.Trim(),
                    _ => null
                } : null;
                if (reason is not { Length: > 0 and <= 500 } || state.PageScreenshotRequested)
                {
                    return JsonSerializer.SerializeToElement(new { success = false, code = "screenshot-request-refused" });
                }

                state.PageScreenshotRequested = true;
                // This transient request only reaches the client carrying this turn; history never reopens a dialog.
                await state.EmitAsync(new NhAssistantPageScreenshotRequestedEvent(reason));
                return JsonSerializer.SerializeToElement(new
                {
                    success = true,
                    code = "screenshot-requested",
                    message = "Consent was requested. No image has been captured or shared. Wait for the user's next message."
                });
            });
    }
}
