// 라벨 양식(종류) 자동 감지 — 라벨마다 특정 위치에 인쇄되는 양식명
// (예: "Rev.A00")을 이용해 라벨 종류를 자동 판별한다.
// 사용자는 규칙마다 (1) 라벨의 어느 영역을 볼지(페이지 비율 좌표),
// (2) 무엇으로 인식할지 — 텍스트 패턴(정규식), 이미지 패턴(학습 템플릿),
// 또는 둘 다 — 를 설정한다. 감지되면 해당 규격이 자동 선택된다.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace LabelSuite.Core;

/// <summary>양식 감지 규칙. Region은 페이지 비율(0~1) 사각형.
/// TextPattern이 비어 있지 않으면 텍스트 검사, UseImage면 이미지 템플릿 검사 —
/// 둘 다 설정하면 어느 한쪽만 맞아도 감지된다(둘 다 맞으면 신뢰도 가산).</summary>
public sealed record LabelFormRule(
    string Name, string Standard,
    (double X, double Y, double W, double H) Region,
    string TextPattern, bool UseImage);

public sealed record FormDetection(string Name, string Standard, double Score,
                                   string Method);

public sealed class LabelFormDetector(string? templatePath = null)
{
    /// <summary>이미지 템플릿 일치로 인정하는 최소 상관계수.</summary>
    public double ImageMinScore { get; set; } = 0.75;

    private const int ThumbW = 64;
    private const int ThumbH = 48;

    private readonly string? _templatePath = templatePath;
    private Dictionary<string, float[]> _templates = LoadTemplates(templatePath);

    // ---------------- 감지 ----------------

    public FormDetection? Detect(IReadOnlyList<LabelFormRule> rules,
                                 IReadOnlyList<OcrWord> words,
                                 SKBitmap image)
    {
        FormDetection? best = null;
        foreach (var rule in rules)
        {
            if (rule.Name.Trim().Length == 0) continue;
            var textOk = false;
            var imageScore = -1.0;

            if (rule.TextPattern.Trim().Length > 0)
                textOk = TextMatches(rule, words, (image.Width, image.Height));
            if (rule.UseImage && _templates.ContainsKey(rule.Name))
                imageScore = ImageScore(rule, image);

            var imageOk = imageScore >= ImageMinScore;
            if (!textOk && !imageOk) continue;

            var score = textOk && imageOk ? 0.5 + imageScore / 2
                      : textOk ? 0.95
                      : imageScore;
            var method = textOk && imageOk ? "텍스트+이미지"
                       : textOk ? "텍스트" : "이미지";
            if (best is null || score > best.Score)
                best = new FormDetection(rule.Name.Trim(), rule.Standard.Trim(),
                                         Math.Min(1, score), method);
        }
        return best;
    }

    private static bool TextMatches(LabelFormRule rule, IReadOnlyList<OcrWord> words,
                                    (int W, int H) pageSize)
    {
        Regex regex;
        try { regex = new Regex(rule.TextPattern.Trim(), RegexOptions.IgnoreCase); }
        catch (ArgumentException) { return false; }
        foreach (var word in words)
        {
            var cx = (word.Bbox.X + word.Bbox.W / 2.0) / Math.Max(1, pageSize.W);
            var cy = (word.Bbox.Y + word.Bbox.H / 2.0) / Math.Max(1, pageSize.H);
            if (cx < rule.Region.X || cx > rule.Region.X + rule.Region.W
                || cy < rule.Region.Y || cy > rule.Region.Y + rule.Region.H) continue;
            if (regex.IsMatch(word.Text.Trim())) return true;
        }
        return false;
    }

    private double ImageScore(LabelFormRule rule, SKBitmap image)
    {
        if (!_templates.TryGetValue(rule.Name, out var template)) return -1;
        var current = ExtractThumb(image, rule.Region);
        if (current is null) return -1;
        double dot = 0;
        for (var i = 0; i < template.Length && i < current.Length; i++)
            dot += template[i] * current[i];
        return dot;   // zero-mean unit-norm → 내적 = 상관계수
    }

    // ---------------- 학습 ----------------

    /// <summary>현재 페이지 이미지의 규칙 영역을 잘라 이미지 템플릿으로 학습한다.</summary>
    public bool LearnTemplate(LabelFormRule rule, SKBitmap image)
    {
        var thumb = ExtractThumb(image, rule.Region);
        if (thumb is null) return false;
        _templates[rule.Name] = thumb;
        SaveTemplates();
        return true;
    }

    public bool HasTemplate(string ruleName) => _templates.ContainsKey(ruleName);

    /// <summary>영역을 잘라 64x48 그레이 썸네일 → zero-mean unit-norm 벡터.</summary>
    internal static float[]? ExtractThumb(SKBitmap image,
                                          (double X, double Y, double W, double H) region)
    {
        var x0 = (int)Math.Clamp(region.X * image.Width, 0, image.Width - 1);
        var y0 = (int)Math.Clamp(region.Y * image.Height, 0, image.Height - 1);
        var x1 = (int)Math.Clamp((region.X + region.W) * image.Width, x0 + 1, image.Width);
        var y1 = (int)Math.Clamp((region.Y + region.H) * image.Height, y0 + 1, image.Height);
        if (x1 - x0 < 4 || y1 - y0 < 4) return null;

        var pixels = new float[ThumbW * ThumbH];
        for (var ty = 0; ty < ThumbH; ty++)
            for (var tx = 0; tx < ThumbW; tx++)
            {
                var sx0 = x0 + tx * (x1 - x0) / ThumbW;
                var sx1 = Math.Max(sx0 + 1, x0 + (tx + 1) * (x1 - x0) / ThumbW);
                var sy0 = y0 + ty * (y1 - y0) / ThumbH;
                var sy1 = Math.Max(sy0 + 1, y0 + (ty + 1) * (y1 - y0) / ThumbH);
                double sum = 0;
                var count = 0;
                for (var sy = sy0; sy < sy1; sy++)
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var color = image.GetPixel(sx, sy);
                        sum += (color.Red * 299 + color.Green * 587 + color.Blue * 114)
                               / 1000.0 / 255.0;
                        count++;
                    }
                pixels[ty * ThumbW + tx] = count > 0 ? (float)(sum / count) : 0;
            }
        return GlyphLibrary.NormalizeVector(pixels);
    }

    // ---------------- 영속 ----------------

    private void SaveTemplates()
    {
        if (_templatePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(
                Path.GetFullPath(_templatePath))!);
            var dto = new JsonObject();
            foreach (var (name, vector) in _templates)
            {
                var bytes = new byte[vector.Length * 4];
                Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
                dto[name] = Convert.ToBase64String(bytes);
            }
            File.WriteAllText(_templatePath, dto.ToJsonString());
        }
        catch (IOException) { }
    }

    private static Dictionary<string, float[]> LoadTemplates(string? path)
    {
        var templates = new Dictionary<string, float[]>();
        if (path is null || !File.Exists(path)) return templates;
        try
        {
            var dto = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (dto is null) return templates;
            foreach (var (name, node) in dto)
            {
                var bytes = Convert.FromBase64String(node!.GetValue<string>());
                var vector = new float[bytes.Length / 4];
                Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
                templates[name] = vector;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException) { }
        return templates;
    }
}
