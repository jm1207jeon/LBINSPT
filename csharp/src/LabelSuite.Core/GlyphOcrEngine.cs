// 패턴 학습 OCR 엔진 — 라벨의 고정 폰트 특성을 이용한 글리프 템플릿 매칭.
//
// 원리: 라벨에 인쇄되는 문자(숫자/영문/붙임표)는 정해져 있고 PDF 렌더링은 매번
// 동일하므로, AWS Textract 검사 결과(단어 위치+텍스트)에서 문자 이미지를 잘라
// "문자→템플릿" 사전을 자동 축적하고, 이후에는 로컬에서 정규화 상관계수(NCC)로
// 문자를 복원한다. 무료·오프라인·초고속이며 학습이 쌓일수록 견고해진다.
using System.Text.Json;
using SkiaSharp;

namespace LabelSuite.Core;

public sealed record GlyphTemplate(float[] Vector, float Aspect);

/// <summary>문자 → 정규화 템플릿(20x28 그레이 + 종횡비) 사전. JSON으로 영속.</summary>
public sealed class GlyphLibrary
{
    public const int GlyphWidth = 20;
    public const int GlyphHeight = 28;
    private const int MaxTemplatesPerChar = 6;
    private const double DuplicateNcc = 0.985;
    private const double AspectRatioLimit = 1.8;   // 종횡비가 이보다 다르면 다른 글자

    private readonly Dictionary<char, List<GlyphTemplate>> _templates = [];
    private readonly object _lock = new();
    public string? Path { get; }

    public GlyphLibrary(string? path = null)
    {
        Path = path;
        if (path is not null && File.Exists(path)) Load(path);
    }

    public int CharCount { get { lock (_lock) return _templates.Count; } }
    public int TemplateCount
    {
        get { lock (_lock) return _templates.Values.Sum(list => list.Count); }
    }

    public void Clear()
    {
        lock (_lock) _templates.Clear();
        Save();
    }

    /// <summary>템플릿 등록 (유사 중복은 무시). vector는 정규화된 상태여야 한다.</summary>
    public bool AddTemplate(char character, GlyphTemplate template)
    {
        lock (_lock)
        {
            if (!_templates.TryGetValue(character, out var list))
            {
                list = [];
                _templates[character] = list;
            }
            if (list.Any(existing => Ncc(existing.Vector, template.Vector) > DuplicateNcc))
                return false;
            if (list.Count >= MaxTemplatesPerChar) list.RemoveAt(0);
            list.Add(template);
            return true;
        }
    }

    /// <summary>가장 유사한 문자와 상관계수(0~1)를 반환. 종횡비가 크게 다르면 배제.</summary>
    public (char Character, double Score) Match(GlyphTemplate candidate)
    {
        lock (_lock)
        {
            var bestChar = '?';
            var bestScore = -1.0;
            foreach (var (character, list) in _templates)
                foreach (var template in list)
                {
                    var ratio = Math.Max(candidate.Aspect, template.Aspect)
                              / Math.Max(0.01f, Math.Min(candidate.Aspect, template.Aspect));
                    if (ratio > AspectRatioLimit) continue;
                    var score = Ncc(template.Vector, candidate.Vector);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestChar = character;
                    }
                }
            return (bestChar, bestScore);
        }
    }

    internal static double Ncc(float[] a, float[] b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;   // 벡터가 zero-mean/unit-norm으로 정규화돼 있어 내적=상관계수
    }

    /// <summary>글리프 픽셀(0~1)을 zero-mean unit-norm 벡터로 정규화.</summary>
    public static float[] NormalizeVector(float[] pixels)
    {
        var mean = pixels.Average();
        var centered = pixels.Select(p => p - mean).ToArray();
        var norm = Math.Sqrt(centered.Sum(v => (double)v * v));
        if (norm < 1e-6) return centered;
        return centered.Select(v => (float)(v / norm)).ToArray();
    }

    // ---------- 영속 ----------

    public void Save()
    {
        if (Path is null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Directory.GetParent(
                System.IO.Path.GetFullPath(Path))!.FullName);
            Dictionary<string, List<string>> dto;
            lock (_lock)
                dto = _templates.ToDictionary(
                    pair => pair.Key.ToString(),
                    pair => pair.Value.Select(t =>
                    {
                        // [aspect][vector...] 순서의 float 블롭
                        var all = new float[t.Vector.Length + 1];
                        all[0] = t.Aspect;
                        t.Vector.CopyTo(all, 1);
                        var bytes = new byte[all.Length * 4];
                        Buffer.BlockCopy(all, 0, bytes, 0, bytes.Length);
                        return Convert.ToBase64String(bytes);
                    }).ToList());
            File.WriteAllText(Path, JsonSerializer.Serialize(dto));
        }
        catch (IOException) { }
    }

    private void Load(string path)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                File.ReadAllText(path));
            if (dto is null) return;
            foreach (var (key, list) in dto)
            {
                if (key.Length != 1) continue;
                foreach (var base64 in list)
                {
                    var bytes = Convert.FromBase64String(base64);
                    var all = new float[bytes.Length / 4];
                    Buffer.BlockCopy(bytes, 0, all, 0, bytes.Length);
                    if (all.Length < 2) continue;
                    _templates.TryAdd(key[0], []);
                    _templates[key[0]].Add(new GlyphTemplate(all[1..], all[0]));
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException) { }
    }
}

public sealed class GlyphOcrEngine(GlyphLibrary library) : IOcrEngine
{
    public string Id => "pattern";
    public string DisplayName => "패턴 학습 (로컬)";
    public GlyphLibrary Library { get; } = library;

    /// <summary>이 상관계수 미만이면 해당 글자를 '?'로 둔다.</summary>
    public double MinScore { get; set; } = 0.55;

    // ================= 학습 =================

    /// <summary>신뢰 가능한 OCR 결과(예: Textract)로부터 글리프를 자동 학습한다.
    /// 반환: 새로 추가된 템플릿 수.</summary>
    public int LearnFrom(SKBitmap image, IReadOnlyList<OcrWord> words)
    {
        var added = 0;
        using var gray = Grayscale(image);
        foreach (var word in words)
        {
            var text = word.Text.Replace(" ", "");
            if (text.Length == 0 || word.Confidence < 90) continue;
            var glyphs = SegmentGlyphs(gray, word.Bbox);
            if (glyphs.Count != text.Length) continue;   // 분할 수 불일치 → 학습 보류
            for (var i = 0; i < glyphs.Count; i++)
            {
                var template = ExtractTemplate(gray, glyphs[i]);
                if (template is not null && Library.AddTemplate(text[i], template)) added++;
            }
        }
        if (added > 0) Library.Save();
        return added;
    }

    // ================= 인식 =================

    public Task<List<OcrWord>> DetectWordsAsync(SKBitmap image,
                                                CancellationToken cancellation = default)
    {
        if (Library.CharCount < 8)
            throw new OcrException(
                "패턴 라이브러리가 아직 비어 있습니다.\n" +
                "AWS Textract 엔진으로 몇 페이지 검사하면 글자 패턴이 자동 학습되고,\n" +
                "그 후 이 엔진을 사용할 수 있습니다. (설정 → OCR 설정에서 학습 상태 확인)");
        return Task.Run(() => Detect(image), cancellation);
    }

    private List<OcrWord> Detect(SKBitmap image)
    {
        using var gray = Grayscale(image);
        var words = new List<OcrWord>();
        foreach (var row in SegmentRows(gray))
        {
            var glyphs = SegmentGlyphs(gray, row);
            if (glyphs.Count == 0) continue;
            var medianWidth = glyphs.Select(g => g.W).OrderBy(w => w)
                .ElementAt(glyphs.Count / 2);
            var gapThreshold = Math.Max(3, (int)(medianWidth * 0.6));

            var current = new List<((int X, int Y, int W, int H) Box, char Ch, double Score)>();
            void Flush()
            {
                if (current.Count == 0) return;
                var known = current.Count(g => g.Ch != '?');
                if (known >= Math.Max(1, current.Count / 2))
                {
                    var x0 = current.Min(g => g.Box.X);
                    var y0 = current.Min(g => g.Box.Y);
                    var x1 = current.Max(g => g.Box.X + g.Box.W);
                    var y1 = current.Max(g => g.Box.Y + g.Box.H);
                    words.Add(new OcrWord(
                        new string(current.Select(g => g.Ch).ToArray()),
                        (x0, y0, x1 - x0, y1 - y0),
                        (int)(current.Average(g => g.Score) * 100)));
                }
                current.Clear();
            }

            (int X, int Y, int W, int H)? previous = null;
            foreach (var glyph in glyphs)
            {
                if (previous is { } prev && glyph.X - (prev.X + prev.W) > gapThreshold)
                    Flush();
                var template = ExtractTemplate(gray, glyph);
                if (template is null) { previous = glyph; continue; }
                var (character, score) = Library.Match(template);
                current.Add((glyph, score >= MinScore ? character : '?', Math.Max(0, score)));
                previous = glyph;
            }
            Flush();
        }
        return words;
    }

    // ================= 이미지 유틸 =================

    private sealed class GrayImage(int width, int height) : IDisposable
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[] Pixels { get; } = new byte[width * height];
        public byte Threshold { get; set; } = 128;
        public bool IsInk(int x, int y) => Pixels[y * Width + x] < Threshold;
        public void Dispose() { }
    }

    private static GrayImage Grayscale(SKBitmap image)
    {
        var gray = new GrayImage(image.Width, image.Height);
        var histogram = new int[256];
        var index = 0;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var color = image.GetPixel(x, y);
                var v = (byte)((color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000);
                gray.Pixels[index++] = v;
                histogram[v]++;
            }
        // 2%/98% 퍼센타일 중간을 문턱으로
        long total = (long)image.Width * image.Height, acc = 0;
        int lo = 0, hi = 255;
        for (var v = 0; v < 256; v++) { acc += histogram[v]; if (acc >= total * 0.02) { lo = v; break; } }
        acc = 0;
        for (var v = 255; v >= 0; v--) { acc += histogram[v]; if (acc >= total * 0.02) { hi = v; break; } }
        gray.Threshold = (byte)((lo + hi) / 2);
        return gray;
    }

    /// <summary>전체 이미지에서 텍스트 행 후보 (가로 잉크 프로젝션).</summary>
    private static List<(int X, int Y, int W, int H)> SegmentRows(GrayImage gray)
    {
        var rowInk = new int[gray.Height];
        for (var y = 0; y < gray.Height; y++)
            for (var x = 0; x < gray.Width; x++)
                if (gray.IsInk(x, y)) rowInk[y]++;
        var rows = new List<(int, int, int, int)>();
        var start = -1;
        for (var y = 0; y < gray.Height; y++)
        {
            var hasInk = rowInk[y] > 0;
            if (hasInk && start < 0) start = y;
            else if (!hasInk && start >= 0)
            {
                if (y - start >= 5) rows.Add((0, start, gray.Width, y - start));
                start = -1;
            }
        }
        if (start >= 0 && gray.Height - start >= 5)
            rows.Add((0, start, gray.Width, gray.Height - start));
        return rows;
    }

    /// <summary>주어진 영역 안에서 세로 잉크 프로젝션으로 글자 상자 분할.</summary>
    private static List<(int X, int Y, int W, int H)> SegmentGlyphs(
        GrayImage gray, (int X, int Y, int W, int H) region)
    {
        var x0 = Math.Max(0, region.X);
        var y0 = Math.Max(0, region.Y);
        var x1 = Math.Min(gray.Width, region.X + region.W);
        var y1 = Math.Min(gray.Height, region.Y + region.H);
        var glyphs = new List<(int, int, int, int)>();
        var start = -1;
        for (var x = x0; x <= x1; x++)
        {
            var hasInk = false;
            if (x < x1)
                for (var y = y0; y < y1 && !hasInk; y++)
                    if (gray.IsInk(x, y)) hasInk = true;
            if (hasInk && start < 0) start = x;
            else if (!hasInk && start >= 0)
            {
                // 세로 범위를 잉크에 맞게 축소
                int top = y1, bottom = y0;
                for (var y = y0; y < y1; y++)
                    for (var gx = start; gx < x; gx++)
                        if (gray.IsInk(gx, y))
                        {
                            if (y < top) top = y;
                            if (y > bottom) bottom = y;
                            break;
                        }
                if (x - start >= 2 && bottom >= top)
                    glyphs.Add((start, top, x - start, bottom - top + 1));
                start = -1;
            }
        }
        return glyphs;
    }

    /// <summary>글리프 영역을 종횡비 보존 20x28 캔버스로 리샘플해 정규화 템플릿 생성.
    /// 이진값 대신 연속 잉크 강도를 써서 획 굵기·곡률 정보를 보존한다.</summary>
    private static GlyphTemplate? ExtractTemplate(GrayImage gray,
                                                  (int X, int Y, int W, int H) glyph)
    {
        if (glyph.W < 2 || glyph.H < 3) return null;
        const int GW = GlyphLibrary.GlyphWidth;
        const int GH = GlyphLibrary.GlyphHeight;
        // 종횡비 보존: 글리프를 캔버스 안에 맞춰 중앙 배치
        var scale = Math.Min((double)GW / glyph.W, (double)GH / glyph.H);
        var destW = Math.Max(1, (int)Math.Round(glyph.W * scale));
        var destH = Math.Max(1, (int)Math.Round(glyph.H * scale));
        var offsetX = (GW - destW) / 2;
        var offsetY = (GH - destH) / 2;

        var pixels = new float[GW * GH];
        for (var ty = 0; ty < destH; ty++)
            for (var tx = 0; tx < destW; tx++)
            {
                var sx0 = glyph.X + tx * glyph.W / destW;
                var sx1 = Math.Max(sx0 + 1, glyph.X + (tx + 1) * glyph.W / destW);
                var sy0 = glyph.Y + ty * glyph.H / destH;
                var sy1 = Math.Max(sy0 + 1, glyph.Y + (ty + 1) * glyph.H / destH);
                double sum = 0;
                var count = 0;
                for (var sy = sy0; sy < sy1 && sy < gray.Height; sy++)
                    for (var sx = sx0; sx < sx1 && sx < gray.Width; sx++)
                    {
                        // 연속 잉크 강도: 문턱 대비 어두운 정도 (0~1)
                        var v = gray.Pixels[sy * gray.Width + sx];
                        var ink = (gray.Threshold * 1.3 - v) / (gray.Threshold * 1.3);
                        sum += Math.Clamp(ink, 0, 1);
                        count++;
                    }
                pixels[(ty + offsetY) * GW + (tx + offsetX)] =
                    count > 0 ? (float)(sum / count) : 0;
            }
        return new GlyphTemplate(GlyphLibrary.NormalizeVector(pixels),
                                 (float)glyph.W / glyph.H);
    }
}
