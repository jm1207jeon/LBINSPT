// OCR 프리페치 워커 — 우선순위 큐 + 세대 태깅 (파이썬 OcrPrefetchWorker 포팅).
// 현재 페이지(priority 0)가 프리페치(priority 1)보다 항상 먼저 처리된다.
// 같은 (세대, 페이지) 중복 제출은 무시해 Textract 이중 과금을 차단한다.
using LabelSuite.Core;
using SkiaSharp;

namespace LabelSuite.App.Services;

public sealed class PrefetchWorker : IDisposable
{
    private sealed record Job(int Priority, long Sequence, int Page, int Generation,
                              string CacheKey, Func<SKBitmap> Render);

    private readonly Func<SKBitmap, Task<PageAnalysis>> _analyze;
    private readonly List<Job> _queue = [];
    private readonly HashSet<(int Generation, int Page)> _pending = [];
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private long _sequence;
    private int _generation;

    public event Action<int, int, string, PageAnalysis>? PageDone;   // gen, page, key, 결과
    public event Action<int, int, string>? PageFailed;               // gen, page, 메시지

    public int Generation => _generation;

    public PrefetchWorker(Func<SKBitmap, Task<PageAnalysis>> analyze)
    {
        _analyze = analyze;
        _ = Task.Run(RunLoopAsync);
    }

    public int NewGeneration()
    {
        lock (_lock)
        {
            _generation++;
            _queue.Clear();
            _pending.Clear();
            return _generation;
        }
    }

    public void Submit(int page, string cacheKey, Func<SKBitmap> render, int priority = 1)
    {
        lock (_lock)
        {
            if (!_pending.Add((_generation, page))) return;
            _queue.Add(new Job(priority, ++_sequence, page, _generation, cacheKey, render));
        }
        _signal.Release();
    }

    public void Prioritize(int page)
    {
        lock (_lock)
        {
            for (var i = 0; i < _queue.Count; i++)
                if (_queue[i].Generation == _generation && _queue[i].Page == page)
                    _queue[i] = _queue[i] with { Priority = 0 };
        }
        _signal.Release();
    }

    private async Task RunLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }

            Job? job = null;
            lock (_lock)
            {
                if (_queue.Count > 0)
                {
                    job = _queue.OrderBy(j => j.Priority).ThenBy(j => j.Sequence).First();
                    _queue.Remove(job);
                }
            }
            if (job is null) continue;
            if (job.Generation != _generation) continue;   // 문서 교체 후 잔여 잡 폐기

            PageAnalysis analysis;
            try
            {
                var image = job.Render();
                analysis = await _analyze(image);
            }
            catch (Exception ex)
            {
                lock (_lock) _pending.Remove((job.Generation, job.Page));
                PageFailed?.Invoke(job.Generation, job.Page, ex.Message);
                continue;
            }
            lock (_lock) _pending.Remove((job.Generation, job.Page));
            PageDone?.Invoke(job.Generation, job.Page, job.CacheKey, analysis);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _signal.Release();
    }
}
