using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NewHeap.Platform.AI.Chat.AspNet.AgentRuns;

/// <summary>
/// <c>POST {prefix}/triggers/{triggerId}</c>: an external system fires a webhook trigger. The call
/// carries no user; it is authenticated by an HMAC-SHA256 signature over
/// <c>{timestamp}.{deliveryId}.{body}</c> with the trigger's shared secret. Calls older or newer
/// than five minutes are refused, and a delivery id starts at most one run.
/// </summary>
internal static class NhAssistantWebhookEndpoints
{
    public const string TimestampHeader = "X-NH-Timestamp";
    public const string SignatureHeader = "X-NH-Signature";
    public const string DeliveryHeader = "X-NH-Delivery";
    internal const int MaxBodyBytes = 64 * 1024;
    internal const int MinimumSecretLength = 32;
    internal static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("triggers/{triggerId}", ReceiveAsync)
            .AllowAnonymous()
            .WithName("NhAssistantFireWebhookTrigger")
            .WithSummary("Fire a webhook trigger")
            .WithDescription("Starts the trigger's agent for the subject the signed body names. Sign `{timestamp}.{deliveryId}.{body}` with HMAC-SHA256 and the shared secret, and send X-NH-Timestamp (Unix seconds), X-NH-Delivery and X-NH-Signature (sha256= plus the lowercase hex digest).")
            .Produces<NhAssistantTriggerReceiptDto>(StatusCodes.Status202Accepted)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status400BadRequest)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status401Unauthorized)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status404NotFound)
            .Produces<NhAssistantErrorDto>(StatusCodes.Status413PayloadTooLarge);
    }

    private static async Task<IResult> ReceiveAsync(
        string triggerId,
        HttpContext httpContext,
        NhAssistantRegistrationState registration)
    {
        var services = httpContext.RequestServices;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(NhAssistantWebhookEndpoints));
        if (!registration.Triggers.TryGetValue(triggerId, out var trigger)
            || trigger.Source != NhAssistantTriggerSource.Webhook
            || trigger.WebhookSecret is null
            || trigger.WebhookSubject is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.TriggerNotFound);
        }

        var secret = trigger.WebhookSecret(services);
        if (string.IsNullOrEmpty(secret) || secret.Length < MinimumSecretLength)
        {
            logger.LogWarning("Webhook trigger {TriggerId} has no usable secret; its calls are refused.", triggerId);
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status404NotFound, NhAssistantErrorCodes.TriggerNotFound);
        }

        if (httpContext.Request.ContentLength > MaxBodyBytes)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status413PayloadTooLarge, NhAssistantErrorCodes.WebhookPayloadInvalid);
        }

        var body = await ReadBodyAsync(httpContext.Request, httpContext.RequestAborted);
        if (body is null)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status413PayloadTooLarge, NhAssistantErrorCodes.WebhookPayloadInvalid);
        }

        var headers = httpContext.Request.Headers;
        var deliveryId = headers[DeliveryHeader].ToString();
        if (!Verify(
                secret,
                headers[TimestampHeader].ToString(),
                deliveryId,
                headers[SignatureHeader].ToString(),
                body,
                DateTimeOffset.UtcNow))
        {
            logger.LogInformation("A call of webhook trigger {TriggerId} was refused: missing, stale or wrong signature.", triggerId);
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status401Unauthorized, NhAssistantErrorCodes.WebhookSignatureInvalid);
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.WebhookPayloadInvalid);
        }

        // The agent's state and the trigger's When filter decide before the subject is read.
        var triggers = services.GetRequiredService<NhAssistantAgentTriggers>();
        var delivery = new NhAssistantWebhookDelivery(deliveryId, root);
        var receipt = await triggers.RefuseEarlyAsync(trigger, delivery, null, httpContext.RequestAborted);
        if (receipt is null)
        {
            if (trigger.WebhookSubject(root) is null)
            {
                return NhAssistantEndpointRouteBuilderExtensions.Error(StatusCodes.Status400BadRequest, NhAssistantErrorCodes.SubjectNotFound);
            }

            receipt = await triggers.FireWebhookAsync(trigger, delivery, httpContext.RequestAborted);
        }

        // Shadow variants of the agent get the delivery too; the caller reads the live receipt.
        await triggers.MirrorWebhookAsync(trigger, delivery, httpContext.RequestAborted);

        return TypedResults.Json(
            new NhAssistantTriggerReceiptDto(receipt.TriggerId, receipt.Outcome, receipt.RunId, receipt.ConversationId, receipt.ReasonCode),
            NhAssistantAgentRunJsonSerializerContext.Default.NhAssistantTriggerReceiptDto,
            statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Checks the timestamp window, the delivery id and the signature in constant time.
    /// </summary>
    internal static bool Verify(
        string secret,
        string timestamp,
        string deliveryId,
        string signature,
        byte[] body,
        DateTimeOffset now)
    {
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !IsValidDeliveryId(deliveryId)
            || !signature.StartsWith("sha256=", StringComparison.Ordinal))
        {
            return false;
        }

        DateTimeOffset sentAt;
        try
        {
            sentAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if ((now - sentAt).Duration() > MaxClockSkew)
        {
            return false;
        }

        var expected = Sign(secret, timestamp, deliveryId, body);
        var actual = Encoding.ASCII.GetBytes(signature["sha256=".Length..]);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), actual);
    }

    /// <summary>
    /// The lowercase hex HMAC-SHA256 of <c>{timestamp}.{deliveryId}.{body}</c>.
    /// </summary>
    internal static string Sign(string secret, string timestamp, string deliveryId, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestamp + "." + deliveryId + ".");
        var material = new byte[prefix.Length + body.Length];
        prefix.CopyTo(material, 0);
        body.CopyTo(material, prefix.Length);
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), material));
    }

    private static bool IsValidDeliveryId(string deliveryId)
    {
        return deliveryId.Length is > 0 and <= 100
               && deliveryId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
