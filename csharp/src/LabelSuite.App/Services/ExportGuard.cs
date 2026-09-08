using System.IO;
using System.Windows;

namespace LabelSuite.App;

/// <summary>파일 내보내기 공통 보호막: 엑셀 등이 파일을 열어 둔 상태(공유 위반)·권한·디스크 부족을
/// 원인별 한국어로 상태바(오류)에 알리고, 검사 결과는 그대로 유지한다. 성공은 상태바(정보)로만.</summary>
public static class ExportGuard
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int DiskFull = unchecked((int)0x80070070);
    private const int NetworkPathNotFound = unchecked((int)0x80070035);
    private const int NetworkNameNotFound = unchecked((int)0x80070043);

    /// <summary>write를 실행하고 결과를 status(메시지, 단계)로 보고한다. 반환: 성공 여부.</summary>
    public static bool Run(string kind, string path, Action write,
                           Action<string, StatusLevel> status)
    {
        var name = Path.GetFileName(path);
        try
        {
            write();
            status($"{kind} 저장 완료: {path}", StatusLevel.Info);
            return true;
        }
        catch (IOException ex) when (ex.HResult is SharingViolation or LockViolation)
        {
            status($"{name} 저장 실패 — 파일이 다른 프로그램(엑셀 등)에서 열려 있는지 확인 후 다시 시도하세요. 검사 결과는 그대로 유지됩니다.",
                   StatusLevel.Error);
        }
        catch (IOException ex) when (ex.HResult == DiskFull)
        {
            status($"{name} 저장 실패 — 디스크 공간이 부족합니다.", StatusLevel.Error);
        }
        catch (IOException ex) when (ex.HResult is NetworkPathNotFound or NetworkNameNotFound)
        {
            status($"{name} 저장 실패 — 네트워크 경로를 찾을 수 없습니다: {Path.GetDirectoryName(path)}",
                   StatusLevel.Error);
        }
        catch (UnauthorizedAccessException)
        {
            status($"{name} 저장 실패 — 폴더에 쓰기 권한이 없습니다: {Path.GetDirectoryName(path)}",
                   StatusLevel.Error);
        }
        catch (DirectoryNotFoundException)
        {
            status($"{name} 저장 실패 — 드라이브/네트워크 연결과 경로를 확인하세요: {Path.GetDirectoryName(path)}",
                   StatusLevel.Error);
        }
        catch (Exception ex)
        {
            AppLog.Error($"{kind} 생성 실패: {path}", ex);
            status($"{name} 생성 실패: {ex.Message} (자세한 내용은 app.log)", StatusLevel.Error);
        }
        return false;
    }

    /// <summary>원인별 안내 문구만 필요할 때 (결과 이미지 저장 등).</summary>
    public static string Describe(Exception ex, string dir) => ex switch
    {
        IOException io when io.HResult is SharingViolation or LockViolation =>
            "파일이 다른 프로그램에서 열려 있습니다. 닫고 다시 시도하세요.",
        IOException io when io.HResult == DiskFull => "디스크 공간이 부족합니다.",
        IOException io when io.HResult is NetworkPathNotFound or NetworkNameNotFound =>
            $"네트워크 경로를 찾을 수 없습니다: {dir}",
        UnauthorizedAccessException => $"폴더에 쓰기 권한이 없습니다: {dir}",
        DirectoryNotFoundException => $"드라이브/네트워크 연결과 경로를 확인하세요: {dir}",
        _ => ex.Message,
    };
}
