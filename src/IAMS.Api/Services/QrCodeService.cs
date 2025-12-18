using QRCoder;

namespace IAMS.Api.Services;

public interface IQrCodeService
{
    byte[] GeneratePng(string content, int pixelsPerModule = 10);
    string GenerateSvg(string content, int pixelsPerModule = 10);
    string GenerateAssetUrl(string assetTag, string? baseUrl = null);
}

public class QrCodeService(IConfiguration configuration) : IQrCodeService
{
    /// <summary>
    /// Generates a QR code as PNG image bytes
    /// </summary>
    /// <param name="content">The content to encode in the QR code</param>
    /// <param name="pixelsPerModule">Size of each module (default 10 = ~330x330 pixels)</param>
    /// <returns>PNG image as byte array</returns>
    public byte[] GeneratePng(string content, int pixelsPerModule = 10)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrCodeData);
        return qrCode.GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// Generates a QR code as SVG string
    /// </summary>
    /// <param name="content">The content to encode in the QR code</param>
    /// <param name="pixelsPerModule">Size of each module (default 10)</param>
    /// <returns>SVG markup as string</returns>
    public string GenerateSvg(string content, int pixelsPerModule = 10)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var svgQrCode = new SvgQRCode(qrCodeData);
        return svgQrCode.GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// Generates a secure asset URL for QR code content
    /// </summary>
    /// <param name="assetTag">The asset tag to include in the URL</param>
    /// <param name="baseUrl">Optional base URL override</param>
    /// <returns>Full URL to the asset</returns>
    public string GenerateAssetUrl(string assetTag, string? baseUrl = null)
    {
        var url = baseUrl ?? configuration["App:BaseUrl"] ?? "https://iams.company.com";
        return $"{url.TrimEnd('/')}/assets/scan/{Uri.EscapeDataString(assetTag)}";
    }
}
