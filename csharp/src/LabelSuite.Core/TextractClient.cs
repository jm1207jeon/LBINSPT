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

    public class ProfileNotFoundException(string profileName) : Exception(
        $"프로필 '{profileName}'을(를) 찾을 수 없습니다. " +
        "설정의 프로필 칸을 비우거나(기본 자격증명 사용) 이름을 확인하세요.");

    private Amazon.Runtime.AWSCredentials? ResolveCredentials()
    {
        if (string.IsNullOrEmpty(profile)) return null;
        var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new ProfileNotFoundException(profile!);
        return credentials;
    }

    /// <summary>SDK 원문 오류를 초보자가 조치할 수 있는 한국어 안내로 변환한다.</summary>
    public static string FriendlyCredentialError(Exception ex)
    {
        if (ex is ProfileNotFoundException) return ex.Message;
        var message = ex.Message ?? "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (message.Contains("EC2 Instance Metadata", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unable to get IAM security credentials",
                                StringComparison.OrdinalIgnoreCase)
            || message.Contains("Unable to find credentials", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Failed to resolve AWS credentials",
                                StringComparison.OrdinalIgnoreCase))
        {
            var credentialsPath = Path.Combine(home, ".aws", "credentials");
            return "자격증명을 읽지 못했습니다.\n" + (File.Exists(credentialsPath)
                ? $"credentials 파일은 존재합니다: {credentialsPath}\n다음을 확인하세요:\n" +
                  "1) 파일명이 credentials.txt가 아닌지 (확장자 없어야 함)\n" +
                  "2) 첫 줄이 [default] 인지\n" +
                  "3) 메모장 저장 인코딩이 'UTF-8' (BOM 아님) 인지\n" +
                  "4) 수정 후 프로그램을 완전히 껐다 다시 실행했는지"
                : $"{credentialsPath} 파일이 없습니다.\n" +
                  "aws configure를 실행하거나 해당 위치에 파일을 만드세요. (README 참고)");
        }
        if (message.Contains("InvalidClientTokenId", StringComparison.OrdinalIgnoreCase)
            || message.Contains("security token included in the request is invalid",
                                StringComparison.OrdinalIgnoreCase))
            return "액세스 키가 잘못되었습니다 (오타 또는 비활성화된 키). " +
                   "AWS IAM에서 키 상태를 확인하세요.";
        if (message.Contains("SignatureDoesNotMatch", StringComparison.OrdinalIgnoreCase))
            return "Secret Key가 잘못되었습니다 (복사 시 일부 누락됐을 수 있음). " +
                   "credentials 파일의 aws_secret_access_key를 다시 확인하세요.";
        if (ex is TaskCanceledException or OperationCanceledException
            || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("NameResolutionFailure", StringComparison.OrdinalIgnoreCase)
            || message.Contains("No such host", StringComparison.OrdinalIgnoreCase))
            return "AWS에 연결할 수 없습니다. 인터넷/방화벽/프록시를 확인하세요.";
        return $"인증 확인 실패: {message}";
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
            return new CredentialStatus(false, Error: FriendlyCredentialError(ex));
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
