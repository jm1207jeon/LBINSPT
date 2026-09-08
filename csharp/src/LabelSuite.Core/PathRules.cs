// 경로·파일명 규칙 — OS API에 기대지 않고 명시적으로 구현해 리눅스 테스트에서도 결정적으로 동작한다.
// (설정의 save_directory 검증, 결과 파일명 정제, 덮어쓰기 방지 접미어)
using System.Text.RegularExpressions;

namespace LabelSuite.Core;

public static class PathRules
{
    private static readonly Regex DrivePath = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);
    private static readonly Regex UncPath = new(@"^\\\\[^\\]+\\[^\\]+", RegexOptions.Compiled);
    private static readonly char[] Forbidden = ['<', '>', '"', '|', '?', '*'];

    /// <summary>결과 저장 폴더로 쓸 수 있는 절대 경로인지.
    /// 허용: 드라이브(C:\…) · UNC(\\서버\공유\…) · 비Windows에서만 '/' 루트.
    /// 금지: 공백, 상대 경로, &lt;&gt;"|?* 및 제어 문자, 드라이브 문자 외 콜론, ".." 세그먼트.
    /// 앞뒤 공백은 Trim 후 판정한다(실제 사용 시에도 Trim해서 쓸 것).</summary>
    public static bool IsValidDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim();
        var body = p;
        if (DrivePath.IsMatch(p)) body = p[2..];
        else if (UncPath.IsMatch(p)) body = p[2..];
        else if (!OperatingSystem.IsWindows() && p.StartsWith('/')) body = p;
        else return false;

        foreach (var c in body)
            if (c < 32 || c == ':' || Forbidden.Contains(c)) return false;
        foreach (var segment in body.Split(['\\', '/']))
            if (segment == "..") return false;
        return true;
    }

    /// <summary>파일 이름 줄기 정제 — 영숫자·'-'·'_'만 남기고 maxLen으로 자른다.
    /// 남는 게 없으면 fallback. (LOT 등 사용자 데이터가 파일명에 들어갈 때)</summary>
    public static string SanitizeFileStem(string? stem, int maxLen = 40, string fallback = "file")
    {
        var kept = new string((stem ?? "")
            .Where(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
                        or '-' or '_')
            .ToArray());
        if (kept.Length > maxLen) kept = kept[..Math.Max(0, maxLen)];
        return kept.Length > 0 ? kept : fallback;
    }

    /// <summary>같은 이름의 파일이 있으면 "이름_(2).확장자", "_(3)"… 로 비켜 간다.
    /// 없으면 그대로 반환.</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem}_({n}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
