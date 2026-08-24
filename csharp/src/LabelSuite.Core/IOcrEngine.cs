// OCR 엔진 추상화 — AWS Textract / 패턴 학습(로컬) / ONNX(로컬)를 설정에서 전환한다.
using SkiaSharp;

namespace LabelSuite.Core;

public interface IOcrEngine
{
    /// <summary>설정 파일에 저장되는 엔진 식별자 (aws / pattern / onnx).</summary>
    string Id { get; }

    /// <summary>사용자 표시명.</summary>
    string DisplayName { get; }

    /// <summary>이미지에서 단어 목록 추출. 실패는 OcrException으로 던진다.</summary>
    Task<List<OcrWord>> DetectWordsAsync(SKBitmap image,
                                         CancellationToken cancellation = default);
}
