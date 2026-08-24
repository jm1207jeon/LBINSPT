// ONNX 로컬 OCR 엔진 — RapidOCR(PP-OCR v5 Latin) 모델을 OnnxRuntime으로 실행.
// 완전 오프라인·무과금. 모델은 패키지에 동봉되어 exe에 함께 배포된다.
using RapidOcrNet;
using SkiaSharp;

namespace LabelSuite.Core;

public sealed class OnnxOcrEngine : IOcrEngine, IDisposable
{
    public string Id => "onnx";
    public string DisplayName => "ONNX RapidOCR (로컬)";

    private readonly object _lock = new();
    private RapidOcr? _ocr;

    private RapidOcr Engine()
    {
        lock (_lock)
        {
            if (_ocr is null)
            {
                var ocr = new RapidOcr();
                // 라벨은 라틴 문자/숫자 → Latin 인식 모델.
                // 모델 경로를 실행 파일 기준 절대 경로로 재지정 (작업 폴더 무관 동작).
                var set = RapidOcrModelSet.PPOCRv5Latin;
                static string Rebase(string path) => System.IO.Path.IsPathRooted(path)
                    ? path : System.IO.Path.Combine(AppContext.BaseDirectory, path);
                set = set with
                {
                    DetModelPath = Rebase(set.DetModelPath),
                    ClsModelPath = Rebase(set.ClsModelPath),
                    RecModelPath = Rebase(set.RecModelPath),
                    KeysPath = Rebase(set.KeysPath),
                };
                ocr.InitModels(set, Math.Max(1, Environment.ProcessorCount / 2));
                _ocr = ocr;
            }
            return _ocr;
        }
    }

    public async Task<List<OcrWord>> DetectWordsAsync(SKBitmap image,
                                                      CancellationToken cancellation = default)
    {
        if (image.Width == 0 || image.Height == 0)
            throw new OcrException("OCR할 이미지가 없습니다.");
        OcrResult result;
        try
        {
            var options = new RapidOcrOptions
            {
                DoAngle = false,          // PDF 렌더는 회전 없음
                MostAngle = false,
                ReturnWordBox = true,     // 단어 단위 박스
            };
            result = await Engine().DetectAsync(image, options, cancellation);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new OcrException($"로컬 ONNX OCR 실패: {ex.Message}", ex);
        }

        var words = new List<OcrWord>();
        foreach (var block in result.TextBlocks ?? [])
        {
            if (block.WordResults is { Length: > 0 } wordBoxes)
            {
                // PP-OCR word box는 글자 단위로 쪼개져 나오는 경우가 많다 → 간격 병합
                var pieces = wordBoxes
                    .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                    .Select(w => (Text: w.Text.Trim(), Box: BoxFromPoints(w.BoxPoints),
                                  Score: (double)w.Score))
                    .OrderBy(p => p.Box.X)
                    .ToList();
                words.AddRange(MergeAdjacentPieces(pieces));
            }
            else if (!string.IsNullOrWhiteSpace(block.Text))
            {
                // 폴백: 라인 텍스트를 공백 기준 분할, bbox는 글자 비례 배분
                words.AddRange(SplitLineToWords(block.Text.Trim(),
                    BoxFromPoints(block.BoxPoints),
                    (int)Math.Clamp(block.BoxScore * 100, 0, 100)));
            }
        }
        return words;
    }

    /// <summary>글자/조각 박스를 수평 간격 기준으로 단어로 병합.</summary>
    internal static List<OcrWord> MergeAdjacentPieces(
        List<(string Text, (int X, int Y, int W, int H) Box, double Score)> pieces)
    {
        var words = new List<OcrWord>();
        if (pieces.Count == 0) return words;
        var medianWidth = pieces
            .Select(p => Math.Max(1, p.Box.W / Math.Max(1, p.Text.Length)))
            .OrderBy(w => w).ElementAt(pieces.Count / 2);
        var gapThreshold = Math.Max(3, (int)(medianWidth * 0.55));

        var text = new System.Text.StringBuilder();
        int x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        double scoreSum = 0;
        var count = 0;
        void Flush()
        {
            if (count == 0) return;
            words.Add(new OcrWord(text.ToString(), (x0, y0, x1 - x0, y1 - y0),
                (int)Math.Clamp(scoreSum / count * 100, 0, 100)));
            text.Clear();
            count = 0;
            scoreSum = 0;
        }
        foreach (var piece in pieces)
        {
            if (count > 0 && piece.Box.X - x1 > gapThreshold) Flush();
            if (count == 0)
            {
                x0 = piece.Box.X; y0 = piece.Box.Y;
                x1 = piece.Box.X + piece.Box.W; y1 = piece.Box.Y + piece.Box.H;
            }
            else
            {
                y0 = Math.Min(y0, piece.Box.Y);
                x1 = Math.Max(x1, piece.Box.X + piece.Box.W);
                y1 = Math.Max(y1, piece.Box.Y + piece.Box.H);
            }
            text.Append(piece.Text);
            scoreSum += piece.Score;
            count++;
        }
        Flush();
        return words;
    }

    private static (int X, int Y, int W, int H) BoxFromPoints(SKPointI[]? points)
    {
        if (points is null || points.Length == 0) return (0, 0, 1, 1);
        var x0 = points.Min(p => p.X);
        var y0 = points.Min(p => p.Y);
        var x1 = points.Max(p => p.X);
        var y1 = points.Max(p => p.Y);
        return (x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    /// <summary>라인 텍스트를 단어로 나누고 bbox를 글자 수 비례로 배분.</summary>
    public static List<OcrWord> SplitLineToWords(string text,
                                                 (int X, int Y, int W, int H) box,
                                                 int confidence)
    {
        var words = new List<OcrWord>();
        var totalChars = text.Length;
        if (totalChars == 0) return words;
        var cursor = 0;
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var start = text.IndexOf(token, cursor, StringComparison.Ordinal);
            if (start < 0) start = cursor;
            var x = box.X + (int)((double)start / totalChars * box.W);
            var w = Math.Max(1, (int)((double)token.Length / totalChars * box.W));
            words.Add(new OcrWord(token, (x, box.Y, w, box.H), confidence));
            cursor = start + token.Length;
        }
        return words;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _ocr?.Dispose();
            _ocr = null;
        }
    }
}
