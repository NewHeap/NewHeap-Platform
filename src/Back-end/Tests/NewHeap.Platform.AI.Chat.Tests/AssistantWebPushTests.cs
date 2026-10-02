using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NewHeap.Platform.AI.Chat.AspNet;
using NewHeap.Platform.AI.Chat.AspNet.Notifications;
using NewHeap.Platform.AI.Chat.Notifications;
using Xunit;

namespace NewHeap.Platform.AI.Chat.Tests;

public sealed class AssistantWebPushTests
{
    [Fact]
    public void Encryption_matches_the_rfc_8291_test_vector()
    {
        // RFC 8291, Appendix A.
        var plaintext = NhAssistantBase64Url.Decode("V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24");
        var serverPublic = NhAssistantBase64Url.Decode(
            "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        var serverPrivate = NhAssistantBase64Url.Decode("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw");
        var userAgentPublic = NhAssistantBase64Url.Decode(
            "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        var salt = NhAssistantBase64Url.Decode("DGv6ra1nlYgDCS1FRnbzlw");
        var authSecret = NhAssistantBase64Url.Decode("BTBZMqHH6r4Tts7J_aSIgg");
        using var serverKey = ImportEcdh(serverPublic, serverPrivate);

        var body = NhAssistantWebPushEncryption.Encrypt(plaintext, userAgentPublic, authSecret, serverKey, salt);

        var header = NhAssistantBase64Url.Decode(
            "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27ml"
            + "mlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        var ciphertext = NhAssistantBase64Url.Decode(
            "8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEs"
            + "bI_0LpXMuGvnzQ");
        Assert.Equal(86, header.Length);
        Assert.Equal([.. header, .. ciphertext], body);
    }

    [Fact]
    public void A_browser_can_decrypt_a_message_with_its_own_keys()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var browserPublic = NhAssistantWebPushKeys.UncompressedPoint(browser.ExportParameters(false).Q);
        var authSecret = RandomNumberGenerator.GetBytes(16);
        var payload = NhAssistantPushPayload.Create("turn-completed", Guid.NewGuid(), "Roadmap", "The answer is ready.");

        var body = NhAssistantWebPushEncryption.Encrypt(payload, browserPublic, authSecret);

        Assert.Equal(payload, Decrypt(body, browser, browserPublic, authSecret));
        Assert.NotEqual(body, NhAssistantWebPushEncryption.Encrypt(payload, browserPublic, authSecret));
    }

    [Fact]
    public void Vapid_authorization_is_a_verifiable_es256_token_for_the_push_service_origin()
    {
        var (publicKey, privateKey) = NhAssistantWebPushKeys.Generate();
        using var signingKey = NhAssistantWebPushKeys.CreateSigningKey(publicKey, privateKey);
        var now = DateTimeOffset.UtcNow;

        var header = NhAssistantVapid.CreateAuthorization(
            signingKey,
            publicKey,
            "mailto:assistant@example.com",
            new Uri("https://fcm.googleapis.com/fcm/send/abc"),
            now);

        Assert.StartsWith("vapid t=", header, StringComparison.Ordinal);
        Assert.EndsWith(", k=" + publicKey, header, StringComparison.Ordinal);
        var token = header["vapid t=".Length..header.IndexOf(", k=", StringComparison.Ordinal)];
        var parts = token.Split('.');
        var claims = JsonDocument.Parse(NhAssistantBase64Url.Decode(parts[1])).RootElement;
        Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
        Assert.Equal("mailto:assistant@example.com", claims.GetProperty("sub").GetString());
        Assert.InRange(claims.GetProperty("exp").GetInt64(), now.ToUnixTimeSeconds(), now.AddHours(24).ToUnixTimeSeconds());

        var point = NhAssistantBase64Url.Decode(publicKey);
        using var verifier = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33], Y = point[33..65] }
        });
        Assert.True(verifier.VerifyData(
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
            NhAssistantBase64Url.Decode(parts[2]),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void Configuration_with_mismatched_keys_or_subject_fails_at_validation()
    {
        var (publicKey, privateKey) = NhAssistantWebPushKeys.Generate();
        var (otherPublic, _) = NhAssistantWebPushKeys.Generate();

        new NhAssistantPushOptions().Validate();
        new NhAssistantPushOptions { PublicKey = publicKey, PrivateKey = privateKey, Subject = "mailto:ops@example.com" }.Validate();
        Assert.Throws<InvalidOperationException>(() =>
            new NhAssistantPushOptions { PublicKey = otherPublic, PrivateKey = privateKey, Subject = "mailto:ops@example.com" }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new NhAssistantPushOptions { PublicKey = publicKey, PrivateKey = privateKey, Subject = "http://example.com" }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new NhAssistantPushOptions { PublicKey = "not-a-key", PrivateKey = privateKey, Subject = "mailto:ops@example.com" }.Validate());
    }

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/token", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/token", true)]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=abc", true)]
    [InlineData("https://web.push.apple.com/token", true)]
    [InlineData("https://notify.windows.com/w", false)]
    [InlineData("http://fcm.googleapis.com/fcm/send/token", false)]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/token", false)]
    [InlineData("https://user@fcm.googleapis.com/fcm/send/token", false)]
    [InlineData("https://fcm.googleapis.com.attacker.example/send", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("https://localhost/push", false)]
    [InlineData("not a url", false)]
    public void Only_endpoints_of_allowed_push_services_are_accepted(string endpoint, bool allowed)
    {
        Assert.Equal(
            allowed,
            NhAssistantPushSubscriptionValidator.TryGetAllowedEndpoint(
                endpoint,
                NhAssistantPushOptions.DefaultAllowedEndpointHosts,
                out _));
    }

    [Fact]
    public void A_subscription_needs_a_valid_browser_key_and_secret()
    {
        var (publicKey, privateKey) = NhAssistantWebPushKeys.Generate();
        var options = new NhAssistantPushOptions { PublicKey = publicKey, PrivateKey = privateKey, Subject = "mailto:ops@example.com" };
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var browserKey = NhAssistantBase64Url.Encode(NhAssistantWebPushKeys.UncompressedPoint(browser.ExportParameters(false).Q));
        var auth = NhAssistantBase64Url.Encode(RandomNumberGenerator.GetBytes(16));
        const string endpoint = "https://fcm.googleapis.com/fcm/send/token";

        var valid = NhAssistantPushSubscriptionValidator.Validate(
            new NhAssistantPushSubscriptionRequest(endpoint, new NhAssistantPushSubscriptionKeysDto(browserKey, auth), "nl-NL"),
            options);

        Assert.NotNull(valid);
        Assert.Equal("nl", valid.Language);
        Assert.Null(NhAssistantPushSubscriptionValidator.Validate(
            new NhAssistantPushSubscriptionRequest(endpoint, new NhAssistantPushSubscriptionKeysDto(browserKey, NhAssistantBase64Url.Encode(new byte[8])), null),
            options));
        Assert.Null(NhAssistantPushSubscriptionValidator.Validate(
            new NhAssistantPushSubscriptionRequest(endpoint, new NhAssistantPushSubscriptionKeysDto(NhAssistantBase64Url.Encode(new byte[65]), auth), null),
            options));
        Assert.Null(NhAssistantPushSubscriptionValidator.Validate(
            new NhAssistantPushSubscriptionRequest("https://attacker.example/push", new NhAssistantPushSubscriptionKeysDto(browserKey, auth), null),
            options));
    }

    [Theory]
    [InlineData(NhAssistantTurnStatuses.Completed, 30, true, "turn-completed")]
    [InlineData(NhAssistantTurnStatuses.Completed, 30, false, "turn-completed-by-other")]
    [InlineData(NhAssistantTurnStatuses.Completed, 3, true, null)]
    [InlineData(NhAssistantTurnStatuses.Failed, 30, true, "turn-failed")]
    [InlineData(NhAssistantTurnStatuses.Failed, 30, false, "turn-failed-by-other")]
    [InlineData(NhAssistantTurnStatuses.WaitingForApproval, 1, true, "approval-required")]
    [InlineData(NhAssistantTurnStatuses.WaitingForApproval, 30, false, null)]
    [InlineData(NhAssistantTurnStatuses.Cancelled, 30, true, null)]
    public void Only_long_turns_and_approvals_for_the_decider_notify(
        string status,
        int seconds,
        bool recipientStartedTurn,
        string? expectedKind)
    {
        var outcome = new NhAssistantTurnOutcome(Guid.NewGuid(), Guid.NewGuid(), "actor-1", status, TimeSpan.FromSeconds(seconds));

        var kind = NhAssistantWebPushNotifier.KindFor(
            outcome,
            recipientStartedTurn ? "actor-1" : "actor-2",
            TimeSpan.FromSeconds(10));

        Assert.Equal(expectedKind, kind);
    }

    [Fact]
    public void Notification_texts_are_localized_and_never_contain_more_than_title_and_status()
    {
        var english = NhAssistantPushTexts.ForTurn("turn-completed-by-other", "en", null, "Sam");
        var dutch = NhAssistantPushTexts.ForTurn("approval-required", "nl", "Planning", "Sam");
        var invitation = NhAssistantPushTexts.ForInvitation("nl", null, "Sam");

        Assert.Equal(("Assistant", "Sam received an answer."), english);
        Assert.Equal(("Planning", "Er wacht een actie op goedkeuring."), dutch);
        Assert.Equal("Sam heeft een gesprek met je gedeeld", invitation.Title);
        var payload = JsonDocument.Parse(NhAssistantPushPayload.Create("invited", Guid.Empty, invitation.Title, invitation.Body)).RootElement;
        Assert.Equal(
            ["type", "kind", "conversationId", "title", "body", "tag"],
            payload.EnumerateObject().Select(property => property.Name));
    }

    internal static byte[] Decrypt(byte[] body, ECDiffieHellman browser, byte[] browserPublic, byte[] authSecret)
    {
        var salt = body[..16];
        var serverPublic = body[21..86];
        using var serverKey = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] }
        });
        var shared = browser.DeriveRawSecretAgreement(serverKey.PublicKey);
        var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(browserPublic).Concat(serverPublic).ToArray();
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        var ciphertext = body[86..^16];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, ciphertext, body[^16..], plaintext);
        Assert.Equal(0x02, plaintext[^1]);
        return plaintext[..^1];
    }

    private static ECDiffieHellman ImportEcdh(byte[] publicKey, byte[] privateKey)
    {
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
            D = privateKey
        });
    }
}
