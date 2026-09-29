using QRCoder;

namespace NewHeap.Platform.AspNet.Common.Authentication.TwoFactor;

/// <summary>
/// Renders QR codes for authenticator enrollment, so clients do not need a QR library.
/// </summary>
public interface INhQrCodeRenderer
{
    /// <summary>
    /// Renders <paramref name="content"/> as a PNG data URI. Pages that show the image need
    /// <c>img-src data:</c> in their content security policy.
    /// </summary>
    string RenderPngDataUri(string content);
}

internal sealed class NhQrCodeRenderer : INhQrCodeRenderer
{
    private const int PixelsPerModule = 6;

    public string RenderPngDataUri(string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(content);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(PixelsPerModule);

        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
