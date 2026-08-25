// PDF 문서 래퍼 — PDFtoImage(PDFium) 기반, 유한 페이지 캐시.
using System.IO;
using SkiaSharp;

namespace LabelSuite.App.Services;

public sealed class PdfDoc : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, SKBitmap> _cache = [];
    private readonly LinkedList<int> _order = [];
    private byte[]? _bytes;

    public string? Path { get; private set; }
    public double Mtime { get; private set; }
    public int PageCount { get; private set; }
    public double RenderZoom { get; set; } = 4.0;   // 72dpi 기준 배율 → dpi = 72*zoom
    public int CachePages { get; set; } = 6;

    public bool IsOpen => _bytes is not null;

    public void Open(string path)
    {
        lock (_lock)
        {
            var bytes = File.ReadAllBytes(path);
            var count = PDFtoImage.Conversion.GetPageCount(bytes);
            if (count == 0) throw new InvalidOperationException("PDF에 페이지가 없습니다.");
            ClearCacheLocked();
            _bytes = bytes;
            Path = path;
            Mtime = new FileInfo(path).LastWriteTimeUtc
                .Subtract(DateTime.UnixEpoch).TotalSeconds;
            PageCount = count;
        }
    }

    public SKBitmap RenderPage(int index)
    {
        lock (_lock)
        {
            if (_bytes is null) throw new InvalidOperationException("열린 PDF가 없습니다.");
            if (index < 0 || index >= PageCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (_cache.TryGetValue(index, out var cached))
            {
                _order.Remove(index);
                _order.AddLast(index);
                return cached;
            }
            var dpi = (int)Math.Round(72 * RenderZoom);
            var bitmap = PDFtoImage.Conversion.ToImage(
                _bytes, page: (Index)index,
                options: new PDFtoImage.RenderOptions(Dpi: dpi));
            _cache[index] = bitmap;
            _order.AddLast(index);
            // 초대형 렌더(고배율)는 캐시 페이지 수를 줄여 메모리 폭주 방지
            var limit = bitmap.ByteCount > 60_000_000 ? 2 : CachePages;
            while (_cache.Count > limit && _order.First is { } oldest)
            {
                _order.RemoveFirst();
                _cache[oldest.Value].Dispose();
                _cache.Remove(oldest.Value);
            }
            return bitmap;
        }
    }

    private void ClearCacheLocked()
    {
        foreach (var bitmap in _cache.Values) bitmap.Dispose();
        _cache.Clear();
        _order.Clear();
        _bytes = null;
        Path = null;
        PageCount = 0;
    }

    /// <summary>렌더 비트맵 캐시만 비운다 (문서는 유지) — 렌더 배율 변경 시 필수.
    /// 배율이 바뀌었는데 이전 배율의 비트맵을 재사용하면 좌표가 어긋난다.</summary>
    public void ClearRenderCache()
    {
        lock (_lock)
        {
            foreach (var bitmap in _cache.Values) bitmap.Dispose();
            _cache.Clear();
            _order.Clear();
        }
    }

    public void Close()
    {
        lock (_lock) ClearCacheLocked();
    }

    public void Dispose() => Close();
}
