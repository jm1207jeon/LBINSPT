// AWS Textract 클라이언트 — 자격증명 검증 + 정확한 bbox 픽셀 환산.
// 실패는 절대 빈 결과로 위장하지 않고 OcrException을 던진다.
using System.Text.RegularExpressions;
using Amazon;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.Textract;
using Amazon.Textract.Model;
using SkiaSharp;

namespace LabelSuite.Core;

public class OcrException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed record CredentialStatus(bool Ok, string? IdentityArn = null, string? Error = null);

public sealed class TextractClient(string region = "ap-northeast-2", string? profile = null)
{
    public const int MaxDimension = 2000;
    private static readonly Regex NonGtinAi =
        new(@"\((17|10|240|30|21)\)", RegexOptions.Compiled);

    private AmazonTextractClient? _client;

    private Amazon.Runtime.AWSCredentials? ResolveCredentials()
    {
        if (string.IsNullOrEmpty(profile)) return null;
        var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
        return chain.TryGetAWSCredentials(profile, out var credentials) ? credentials : null;
    }

    public async Task<CredentialStatus> ValidateCredentialsAsync(
        CancellationToken cancellation = default)
    {
        try
        {
            var config = new AmazonSecurityTokenServiceConfig
            { RegionEndpoint = RegionEndpoint.GetBySystemName(region), Timeout = TimeSpan.FromSeconds(5) };
            using var sts = ResolveCredentials() is { } credentials
                ? new AmazonSecurityTokenServiceClient(credentials, config)
                : new AmazonSecurityTokenServiceClient(config);
            var identity = await sts.GetCallerIdentityAsync(
                new GetCallerIdentityRequest(), cancellation);
            return new CredentialStatus(true, identity.Arn);
        }
        catch (Exception ex)
        {
            return new CredentialStatus(false, Error: ex.Message);
        }
    }

    private AmazonTextractClient Client()
    {
        if (_client is null)
        {
            var config = new AmazonTextractConfig
            { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
            _client = ResolveCredentials() is { } credentials
                ? new AmazonTextractClient(credentials, config)
                : new AmazonTextractClient(config);
        }
        return _client;
    }

    /// <summary>Textract 권장 크기로 리사이즈 후 JPEG 인코딩.</summary>
    public static byte[] EncodeJpeg(SKBitmap image)
    {
        var width = image.Width;
        var height = image.Height;
        SKBitmap toEncode = image;
        if (width > MaxDimension || height > MaxDimension)
        {
            var scale = Math.Min((double)MaxDimension / width, (double)MaxDimension / height);
            toEncode = image.Resize(
                new SKImageInfo((int)(width * scale), (int)(height * scale)),
                SKFilterQuality.High) ?? image;
        }
        using var skImage = SKImage.FromBitmap(toEncode);
        using var data = skImage.Encode(SKEncodedImageFormat.Jpeg, 85);
        if (!ReferenceEquals(toEncode, image)) toEncode.Dispose();
        return data.ToArray();
    }

    public async Task<List<OcrWord>> DetectWordsAsync(SKBitmap image,
                                                      CancellationToken cancellation = default)
    {
        if (image.Width == 0 || image.Height == 0)
            throw new OcrException("OCR할 이미지가 없습니다.");
        var payload = EncodeJpeg(image);

        DetectDocumentTextResponse response;
        try
        {
            response = await Client().DetectDocumentTextAsync(new DetectDocumentTextRequest
            { Document = new Document { Bytes = new MemoryStream(payload) } }, cancellation);
        }
        catch (Amazon.Runtime.AmazonServiceException ex)
        {
            throw new OcrException($"AWS Textract 호출 실패: {ex.Message}", ex);
        }
        catch (Amazon.Runtime.AmazonClientException ex)
        {
            throw new OcrException(
                "AWS 자격증명/네트워크 오류입니다. 설정에서 자격증명을 확인하세요.\n" + ex.Message, ex);
        }

        var words = new List<OcrWord>();
        foreach (var block in response.Blocks)
        {
            if (block.BlockType != BlockType.WORD || string.IsNullOrWhiteSpace(block.Text))
                continue;
            var text = block.Text.Trim()
                .Replace('—', '-').Replace('–', '-').Replace("©", "(C)");
            // 단독 비-GTIN AI 조각 제외 — (01) 포함 문자열은 GTIN 검사용으로 유지
            if (!text.Contains("(01)") && NonGtinAi.IsMatch(text)) continue;
            var box = block.Geometry?.BoundingBox;
            if (box is null) continue;
            words.Add(new OcrWord(text,
                ((int)(box.Left * image.Width), (int)(box.Top * image.Height),
                 (int)(box.Width * image.Width), (int)(box.Height * image.Height)),
                (int)block.Confidence));
        }
        return words;
    }
}
