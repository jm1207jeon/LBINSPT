// 라벨 문서번호 → 검사 규격 자동 판별.
// 라벨(메인 라벨 우측 중앙 등)에 인쇄된 양식 문서번호·개정("PML-001(Rev.1)", "BSL-01", "Rev.A00")을
// OCR 단어에서 찾아 standards.json의 규격에 대응시킨다. 규격 표시명(display_name)에서 문서번호와
// Rev를 자동으로 유도하므로 별도 설정 없이 동작하고, 규격별 doc_patterns(정규식)로 보강할 수 있다.
// 목록(엑셀)의 STANDARD 열이나 국가별 매핑은 더 이상 규격 선택에 쓰지 않는다 — 라벨에서 읽은 값만 적용.
using System.Text;
using System.Text.RegularExpressions;

namespace LabelSuite.Core;

/// <summary>감지 결과. Standard가 null이면 문서번호는 읽었지만 Rev를 못 읽어 후보가 여럿(Ambiguous).</summary>
public sealed record StandardDetection(
    string? Standard, string MatchedText, (int X, int Y, int W, int H) Bbox,
    string Basis, double Score, IReadOnlyList<string> Candidates)
{
    public bool Ambiguous => Standard is null;
}

public static class StandardDetector
{
    /// <summary>규격 하나의 라벨 서명 — 표시명 "PML-001(Rev.1)" → Doc "PML-001", Rev "1";
    /// "A00" → Rev "A00"(라벨에는 "Rev.A00"으로 인쇄); 그 외는 표시명 전체를 문서번호로 본다.</summary>
    public sealed record Signature(string Standard, string? DocNumber, string? Rev,
                                   IReadOnlyList<Regex> Extra)
    {
        public Regex? DocRegex { get; init; }
        public Regex? RevRegex { get; init; }
        /// <summary>Rev만으로도 규격을 특정할 수 있는지 (A00처럼 3자 이상 고유 토큰).</summary>
        public bool RevDistinctive => Rev is { Length: >= 3 };
    }

    private static readonly Regex DisplayWithRev =
        new(@"^(?<doc>.+?)\s*\(\s*Rev\.?\s*(?<rev>[^)]+?)\s*\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RevOnlyName =
        new(@"^[A-Z]\d{2,3}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<Signature> Signatures(IEnumerable<StandardSpec> specs)
    {
        var list = new List<Signature>();
        foreach (var spec in specs)
        {
            var display = spec.DisplayName.Trim();
            string? doc = null, rev = null;
            var m = DisplayWithRev.Match(display);
            if (m.Success) { doc = m.Groups["doc"].Value.Trim(); rev = m.Groups["rev"].Value.Trim(); }
            else if (RevOnlyName.IsMatch(display)) rev = display;
            else if (display.Any(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')))
                doc = display;   // 라틴/숫자가 없는 표시명(예: "중국")은 라벨 문서번호 감지 대상이 아니다
            var extra = new List<Regex>();
            foreach (var pattern in spec.DocPatternList)
            {
                try { extra.Add(new Regex(pattern, RegexOptions.IgnoreCase)); }
                catch (ArgumentException) { /* 잘못된 정규식은 무시 */ }
            }
            if (doc is null && rev is null && extra.Count == 0) continue;
            list.Add(new Signature(spec.Name, doc, rev, extra)
            {
                DocRegex = doc is null ? null : new Regex(
                    @"(?<![A-Z0-9])" + Fuzzy(doc) + @"(?![A-Z0-9])", RegexOptions.IgnoreCase),
                RevRegex = rev is null ? null : new Regex(
                    @"REV[.,:]?\s*" + Fuzzy(rev) + @"(?![A-Z0-9])", RegexOptions.IgnoreCase),
            });
        }
        return list;
    }

    /// <summary>OCR 혼동 문자를 허용하는 정규식 조각 — 0/O, 1/I/l, 5/S, 8/B, 2/Z, 6/G, 구분자 생략.</summary>
    internal static string Fuzzy(string literal)
    {
        var sb = new StringBuilder();
        foreach (var ch in literal.ToUpperInvariant())
            sb.Append(ch switch
            {
                '0' or 'O' or 'Q' or 'D' => "[0OQD]",
                '1' or 'I' or 'L' => "[1IL|]",
                '2' or 'Z' => "[2Z]",
                '5' or 'S' => "[5S]",
                '8' or 'B' => "[8B]",
                '6' or 'G' => "[6G]",
                '-' or '–' or '—' or '_' or '~' => "[-–—_~ ]?",
                '.' or ',' => "[.,]?",
                ' ' => @"\s*",
                _ => Regex.Escape(ch.ToString()),
            });
        return sb.ToString();
    }

    /// <summary>OCR 단어에서 규격을 판별한다. 우선순위: doc_patterns 일치 > 문서번호+Rev > 고유 Rev(A00)
    /// > 문서번호만(후보 1개일 때). 문서번호만 읽혔는데 후보가 여럿(PML-001 → Rev.0/Rev.1)이면
    /// Standard=null(Ambiguous)로 반환해 UI가 이전 규격을 유지하고 수동 선택을 안내한다.</summary>
    public static StandardDetection? Detect(IEnumerable<StandardSpec> specs, IReadOnlyList<OcrWord> words)
    {
        var signatures = Signatures(specs);
        if (signatures.Count == 0 || words.Count == 0) return null;
        var texts = Texts(words);
        StandardDetection? best = null;
        void Offer(StandardDetection d)
        {
            if (best is null || d.Score > best.Score) best = d;
        }

        // 문서번호만 일치한 규격들(Rev 미확인) — 뒤에서 모호성 판단
        var docOnly = new List<(Signature Sig, OcrWord Word)>();
        foreach (var sig in signatures)
        {
            foreach (var extra in sig.Extra)
                foreach (var (text, word) in texts)
                    if (extra.IsMatch(text))
                        Offer(new StandardDetection(sig.Standard, word.Text.Trim(), word.Bbox, "패턴", 1.0, [sig.Standard]));

            var docWords = texts.Where(t => sig.DocRegex?.IsMatch(t.Text) == true).Select(t => t.Word).ToList();
            var revWords = texts.Where(t => sig.RevRegex?.IsMatch(t.Text) == true).Select(t => t.Word).ToList();
            if (sig.DocNumber is not null && sig.Rev is not null)
            {
                // 문서번호와 Rev는 보통 두 줄로 인쇄된다 ("PML-001" 아랫줄에 "Rev.1") — 문서번호 바로 아래/옆의
                // Rev를 우선 짝짓고, 인접한 Rev가 없을 때만 페이지 어딘가의 Rev를 낮은 점수로 쓴다
                (OcrWord Doc, OcrWord Rev)? pair = null;
                foreach (var d in docWords)
                {
                    foreach (var r in revWords)
                        if (Adjacent(d, r)) { pair = (d, r); break; }
                    if (pair is not null) break;
                }
                var adjacent = pair is not null;
                if (pair is null && docWords.Count > 0 && revWords.Count > 0) pair = (docWords[0], revWords[0]);
                if (pair is { } p)
                    Offer(new StandardDetection(sig.Standard, Join(p.Doc, p.Rev), Union(p.Doc.Bbox, p.Rev.Bbox),
                                                adjacent ? "문서번호+Rev" : "문서번호+Rev(원거리)",
                                                adjacent ? 0.95 : 0.85, [sig.Standard]));
                else if (docWords.Count > 0) docOnly.Add((sig, docWords[0]));
                else if (revWords.Count > 0 && sig.RevDistinctive)
                    Offer(new StandardDetection(sig.Standard, revWords[0].Text.Trim(), revWords[0].Bbox, "Rev", 0.8, [sig.Standard]));
            }
            else if (sig.Rev is not null)   // 표시명 자체가 Rev 토큰 (A00)
            {
                if (revWords.Count > 0)
                    Offer(new StandardDetection(sig.Standard, revWords[0].Text.Trim(), revWords[0].Bbox, "Rev", 0.9, [sig.Standard]));
            }
            else if (sig.DocNumber is not null && docWords.Count > 0)
                docOnly.Add((sig, docWords[0]));
        }
        if (best is not null) return best;
        if (docOnly.Count == 0) return null;
        // 같은 문서번호를 공유하는 규격이 여럿이면 모호 — 후보 목록만 돌려준다
        var grouped = docOnly.GroupBy(d => d.Sig.DocNumber!, StringComparer.OrdinalIgnoreCase)
                             .OrderByDescending(g => g.Count()).First();
        var candidates = grouped.Select(d => d.Sig.Standard).Distinct().ToList();
        var first = grouped.First();
        return candidates.Count == 1
            ? new StandardDetection(first.Sig.Standard, first.Word.Text.Trim(), first.Word.Bbox, "문서번호", 0.7, candidates)
            : new StandardDetection(null, first.Word.Text.Trim(), first.Word.Bbox, "문서번호(Rev 미인식)", 0.5, candidates);
    }

    /// <summary>검색 대상 문자열: 단어 하나 + 같은 줄에서 이웃한 2~3단어 결합("Rev." + "A00", "PML-001" + "(Rev.1)").</summary>
    private static List<(string Text, OcrWord Word)> Texts(IReadOnlyList<OcrWord> words)
    {
        var result = new List<(string, OcrWord)>();
        foreach (var w in words) result.Add((w.Text.Trim(), w));
        var lines = words.OrderBy(w => w.Bbox.Y).ThenBy(w => w.Bbox.X).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var run = new List<OcrWord> { lines[i] };
            for (var j = i + 1; j < lines.Count && run.Count < 3; j++)
            {
                var prev = run[^1];
                var next = lines[j];
                var sameLine = Math.Abs((next.Bbox.Y + next.Bbox.H / 2.0) - (prev.Bbox.Y + prev.Bbox.H / 2.0))
                               <= Math.Max(prev.Bbox.H, next.Bbox.H) * 0.6;
                var adjacent = next.Bbox.X >= prev.Bbox.X && next.Bbox.X - (prev.Bbox.X + prev.Bbox.W)
                               <= Math.Max(prev.Bbox.H, next.Bbox.H) * 1.5;
                if (!sameLine || !adjacent) continue;
                run.Add(next);
                var joined = string.Join(" ", run.Select(r => r.Text.Trim()));
                var bbox = run.Skip(1).Aggregate(run[0].Bbox, (acc, r) => Union(acc, r.Bbox));
                result.Add((joined, new OcrWord(joined, bbox, run.Min(r => r.Confidence))));
            }
        }
        return result;
    }

    /// <summary>Rev 단어가 문서번호 단어와 인접한가 — 같은 줄 오른쪽, 또는 바로 아랫줄/윗줄(줄 높이 2.2배 이내)에서
    /// 가로로 겹치거나 가까움. 같은 단어(결합 텍스트)면 참.</summary>
    internal static bool Adjacent(OcrWord doc, OcrWord rev)
    {
        if (ReferenceEquals(doc, rev) || doc.Bbox == rev.Bbox) return true;
        var lineH = Math.Max(1, Math.Max(doc.Bbox.H, rev.Bbox.H));
        var docCy = doc.Bbox.Y + doc.Bbox.H / 2.0;
        var revCy = rev.Bbox.Y + rev.Bbox.H / 2.0;
        var sameLine = Math.Abs(revCy - docCy) <= lineH * 0.6
                       && rev.Bbox.X >= doc.Bbox.X
                       && rev.Bbox.X - (doc.Bbox.X + doc.Bbox.W) <= lineH * 3;
        var horizontallyNear = rev.Bbox.X + rev.Bbox.W > doc.Bbox.X - lineH * 2
                               && rev.Bbox.X < doc.Bbox.X + doc.Bbox.W + lineH * 2;
        var stacked = Math.Abs(revCy - docCy) <= lineH * 2.2 && horizontallyNear;
        return sameLine || stacked;
    }

    private static string Join(OcrWord a, OcrWord b) =>
        ReferenceEquals(a, b) || a.Text == b.Text ? a.Text.Trim() : $"{a.Text.Trim()} {b.Text.Trim()}";

    private static (int X, int Y, int W, int H) Union((int X, int Y, int W, int H) a, (int X, int Y, int W, int H) b)
    {
        var x0 = Math.Min(a.X, b.X);
        var y0 = Math.Min(a.Y, b.Y);
        var x1 = Math.Max(a.X + a.W, b.X + b.W);
        var y1 = Math.Max(a.Y + a.H, b.Y + b.H);
        return (x0, y0, x1 - x0, y1 - y0);
    }
}
