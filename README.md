# LabelSuite — 통합 라벨 검사 프로그램

기존 두 프로그램(`Label Inspector_list generator`의 목록 생성, `Label Inspector`의
라벨 OCR 검사)을 하나의 앱으로 통합·고도화한 프로그램입니다.

**두 가지 구현이 있습니다:**

| 구현 | 위치 | 배포물 | 상태 |
|---|---|---|---|
| **C# (.NET 8 + WPF)** — 권장 | `csharp/` | 자립형 **단일 exe** (~80MB, 설치·런타임 불필요) | 신규 — 장기 유지 대상 |
| Python (PySide6) | `labelsuite/` | PyInstaller onedir zip (~500MB) | 검증된 참조 구현 |

두 구현은 동일한 로직(스키마·생성 규칙·GS1 파서·검사 엔진·프리페치·캐시)을
공유하며, C# 쪽은 파이썬 테스트 스위트를 명세로 포팅한 xunit 70건으로 동등성을
보장합니다. OCR은 양쪽 다 AWS Textract(서버 측 인식)라 인식률이 동일합니다.

## C# 버전 (csharp/)

```bash
# 빌드/테스트 (Windows 또는 Linux)
dotnet test csharp/tests/LabelSuite.Core.Tests
# 실행 (Windows)
dotnet run --project csharp/src/LabelSuite.App
# 단일 exe 배포 (Windows)
dotnet publish csharp/src/LabelSuite.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

GitHub Actions: **Actions 탭 → "Build C# Windows EXE" → Run workflow**,
또는 `cs-v1.0.0` 형식 태그를 푸시하면 Release에 단일 exe zip이 첨부됩니다.
고급 설정(컬럼 매핑·중국 REF 매핑)은 설정 창의 "설정 폴더 열기"로 JSON을 직접
편집합니다.

---

## Python 버전 (labelsuite/)

## 주요 기능

| 탭 | 기능 |
|---|---|
| **목록 생성** | 주문일정/품목번호/BSC 엑셀 3종 → 검사 목록 생성. 연/월/일 체크박스 날짜 선택, 행 단위 경고/오류 패널, 생성 즉시 검사 탭으로 전달 |
| **라벨 검사** | 라벨 PDF를 AWS Textract OCR + 바코드(DataMatrix 포함) 검사. **PDF 로드 시 전 페이지 백그라운드 선행 OCR** — 페이지 이동 시 딜레이 없이 결과 표시. LOT 자동 매칭, 규격 자동 선택, GS1 교차 검증, 합불 판정과 주석 이미지 저장. PDF 드래그앤드롭 지원 |
| **검사 이력** | SQLite 이력 조회/필터, LOT별 리포트(xlsx) 내보내기 |

레거시 대비 주요 수정: 합불 판정 불가 버그, 저장 이미지 색상 반전, OCR 중
페이지 이동 시 결과 잔류, PDF 메모리 폭주, GTIN 13/14자리 불일치, 2/29 제조분
행 소멸, 날짜 트리 클릭 오프바이원, 파일 카운터 리셋 등.

## 실행 (개발)

```bash
pip install -e .
python -m labelsuite
```

- Python 3.11+, 테스트: `pip install -e .[dev] && pytest`
- AWS 자격증명: `aws configure` 또는 환경변수. 앱 시작 시 상태바에서 인증
  상태를 확인할 수 있고, 설정 → AWS 탭에서 "인증 확인"이 가능합니다.
- 설정/이력/OCR 캐시는 사용자 폴더(`%APPDATA%\LabelSuite` 또는
  `~/.config·~/.local/share/LabelSuite`)에 저장됩니다.

## Windows exe 빌드

### 방법 1 — GitHub Actions (권장, Windows 머신 불필요)

- **수동 빌드**: GitHub 저장소의 **Actions 탭 → "Build Windows EXE" → Run
  workflow** 실행 후, 완료된 잡의 Artifacts에서
  `LabelSuite-windows-*.zip`을 내려받습니다.
- **릴리스 빌드**: 버전 태그를 푸시하면 자동으로 GitHub Release가 만들어지고
  zip이 첨부됩니다.

  ```bash
  git tag v1.0.0
  git push origin v1.0.0
  ```

빌드 파이프라인은 테스트(94건) → PyInstaller(onedir) → exe 스모크 확인 →
zip 패키징 순으로 수행됩니다.

### 방법 2 — 로컬 Windows 머신

```bat
packaging\build_win.bat
```

`dist\LabelSuite\LabelSuite.exe`가 생성됩니다(onedir). `dist\LabelSuite` 폴더를
zip으로 배포하면 어느 PC에서나 압축 해제 후 바로 실행할 수 있습니다.

## 구조

```
labelsuite/
├── core/          # GUI 비의존 순수 로직 (스키마·생성·검사·OCR·바코드·이력)
├── gui/           # PySide6 탭·위젯·워커
└── resources/     # 기본 설정 JSON (규격 카운트·컬럼 매핑·설정)
tests/             # pytest (오프스크린 GUI 테스트 포함)
packaging/         # PyInstaller spec + Windows 빌드 스크립트
docs/              # 수동 검증 체크리스트
```

레거시 폴더(`Label Inspector/`, `Label Inspector_list generator/`)는 실데이터
패리티 검증 완료 시까지 참조용으로 유지됩니다.

## 검사 목록 파일 계약

- 시트 `Label Inspection List`, 헤더:
  `LOT, PRODUCTS, PN, REF, MFG DATE, EXP DATE, GTIN[, STANDARD]`
- 구버전 7컬럼 파일도 읽을 수 있으며, GTIN은 로드 시 GTIN-14로 정규화됩니다.
- `STANDARD` 값(MDR/MDD/BSC/A00/A02/중국)이 있으면 검사 탭에서 규격이 자동
  선택됩니다.
