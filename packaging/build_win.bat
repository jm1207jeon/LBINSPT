@echo off
rem LabelSuite Windows 빌드 스크립트
rem 사용법: Windows에서 저장소 루트 기준 packaging\build_win.bat 실행

setlocal
cd /d "%~dp0.."

echo [1/4] 가상환경 생성/활성화...
if not exist .venv (
    py -3.11 -m venv .venv || python -m venv .venv
)
call .venv\Scripts\activate.bat

echo [2/4] 의존성 설치...
python -m pip install --upgrade pip
pip install -e .[dev]
if errorlevel 1 goto :error

echo [3/4] 테스트 실행...
python -m pytest tests -q
if errorlevel 1 goto :error

echo [4/4] PyInstaller 빌드 (onedir)...
pyinstaller packaging\labelsuite.spec --noconfirm --distpath dist --workpath build
if errorlevel 1 goto :error

echo.
echo 빌드 완료: dist\LabelSuite\LabelSuite.exe
echo 배포 시 dist\LabelSuite 폴더 전체를 zip으로 압축해 전달하세요.
echo 실행 데이터(설정/이력/캐시)는 %%APPDATA%%\LabelSuite 에 저장됩니다.
exit /b 0

:error
echo 빌드 실패 — 위 오류를 확인하세요.
exit /b 1
