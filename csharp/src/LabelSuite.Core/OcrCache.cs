// 페이지 분석 결과 캐시 — OCR 단어 + 바코드를 한 키로 보관 (재과금 방지).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LabelSuite.Core;

public sealed record BarcodeHit(
    string Symbology, string Text, (int X, int Y, int W, int H) Bbox, bool IsGs1);

public sealed class PageAnalysis
{
    public List<OcrWord> Words { get; init; } = [];
    public List<BarcodeHit> Barcodes { get; init; } = [];
}

public sealed class OcrCache(string? directory, int maxEntries = 500)
{
    private readonly LinkedList<string> _order = [];
    private readonly Dictionary<string, (LinkedListNode<string> Node, PageAnalysis Value)> _memory = [];
    private readonly object _lock = new();

    public string? Directory { get; } = InitDirectory(directory);
    public int MaxEntries { get; } = maxEntries;

    private static string? InitDirectory(string? directory)
    {
        if (directory is not null) System.IO.Directory.CreateDirectory(directory);
        return directory;
    }

    public static string PageKey(string docPath, double mtime, int page,
                                 double renderZoom, string preprocessSig)
    {
        var raw = FormattableString.Invariant(
            $"{docPath}|{mtime:F3}|{page}|{renderZoom:F2}|{preprocessSig}");
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)))
            .ToLowerInvariant();
    }

    // ---- JSON 직렬화 형태 (파이썬 버전과 호환) ----
    private sealed record WordDto(string text, int[] bbox, int confidence);
    private sealed record BarcodeDto(string symbology, string text, int[] bbox, bool is_gs1);
    private sealed record PageDto(List<WordDto> words, List<BarcodeDto> barcodes);

    public PageAnalysis? Get(string key)
    {
        lock (_lock)
        {
            if (_memory.TryGetValue(key, out var entry))
            {
                _order.Remove(entry.Node);
                _order.AddLast(entry.Node);
                return entry.Value;
            }
        }
        if (Directory is null) return null;
        var path = Path.Combine(Directory, $"{key}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<PageDto>(File.ReadAllText(path));
            if (dto is null) return null;
            var analysis = new PageAnalysis
            {
                Words = dto.words.Select(w => new OcrWord(
                    w.text, (w.bbox[0], w.bbox[1], w.bbox[2], w.bbox[3]), w.confidence)).ToList(),
                Barcodes = dto.barcodes.Select(b => new BarcodeHit(
                    b.symbology, b.text, (b.bbox[0], b.bbox[1], b.bbox[2], b.bbox[3]),
                    b.is_gs1)).ToList(),
            };
            Remember(key, analysis);
            return analysis;
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    public void Put(string key, PageAnalysis analysis)
    {
        Remember(key, analysis);
        if (Directory is null) return;
        try
        {
            var dto = new PageDto(
                analysis.Words.Select(w => new WordDto(
                    w.Text, [w.Bbox.X, w.Bbox.Y, w.Bbox.W, w.Bbox.H], w.Confidence)).ToList(),
                analysis.Barcodes.Select(b => new BarcodeDto(
                    b.Symbology, b.Text, [b.Bbox.X, b.Bbox.Y, b.Bbox.W, b.Bbox.H],
                    b.IsGs1)).ToList());
            File.WriteAllText(Path.Combine(Directory, $"{key}.json"),
                              JsonSerializer.Serialize(dto));
            PruneDisk();
        }
        catch (IOException) { /* 디스크 캐시 실패는 치명적이지 않음 */ }
    }

    private void Remember(string key, PageAnalysis analysis)
    {
        lock (_lock)
        {
            if (_memory.TryGetValue(key, out var existing))
                _order.Remove(existing.Node);
            var node = _order.AddLast(key);
            _memory[key] = (node, analysis);
            while (_memory.Count > MaxEntries && _order.First is { } oldest)
            {
                _order.RemoveFirst();
                _memory.Remove(oldest.Value);
            }
        }
    }

    private void PruneDisk()
    {
        if (Directory is null) return;
        var files = new DirectoryInfo(Directory).GetFiles("*.json")
            .OrderBy(f => f.LastWriteTimeUtc).ToList();
        foreach (var file in files.Take(Math.Max(0, files.Count - MaxEntries)))
        {
            try { file.Delete(); } catch (IOException) { }
        }
    }
}
