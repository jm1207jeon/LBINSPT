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
            while (_cache.Count > CachePages && _order.First is { } oldest)
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

    public void Close()
    {
        lock (_lock) ClearCacheLocked();
    }

    public void Dispose() => Close();
}
