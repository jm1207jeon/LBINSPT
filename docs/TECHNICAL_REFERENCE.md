# LaVIS 기술 명세서 (Technical Reference)

| 항목 | 내용 |
|---|---|
| 제품명 | **LaVIS** — Label Verification & Inspection System |
| 창 제목 | `LaVIS - Label Verification & Inspection System Rev.00` |
| 문서 버전 | 1.0 (2026-09-11) — 현재 구현 기준, 브랜치 `claude/label-inspector-integration-ef3hla` |
| 대상 독자 | 유지보수 개발자, 검증(밸리데이션) 담당자, 인수인계 대상자 |
| 범위 | 목적·배경부터 알고리즘 수식·구현 코드·데이터 계약·UI 동작까지 전 기능 |
| 코드 규모 | Core 6,617행 / App 5,589행 / 테스트 310건 (28개 파일, 265개 테스트 메서드) |
| 관련 문서 | `docs/PRD.md`(요구사항), `docs/USER_GUIDE.html`(사용자), `docs/family_design.md`(디자인 규범), `docs/manual_checklist.md`(실기 검증) |

> **읽는 법** — §1~§4는 배경과 구조, §5~§14는 엔진 내부(알고리즘·수식·코드), §15는 UI/UX,
> §16~§21은 데이터·운영·품질입니다. 특정 기능만 볼 때는 §4의 파일별 책임표에서 파일명을 찾아
> 해당 절로 이동하세요. 모든 수식은 실제 구현과 1:1 대응하며 근거 코드를 함께 인용했습니다.

---

## 목차

1. [목적과 배경](#1-목적과-배경)
2. [시스템 개요](#2-시스템-개요)
3. [기술 스택](#3-기술-스택)
4. [프로젝트 구조와 파일별 책임](#4-프로젝트-구조와-파일별-책임)
5. [데이터 모델](#5-데이터-모델)
6. [이미지 파이프라인](#6-이미지-파이프라인)
7. [OCR 엔진 계층](#7-ocr-엔진-계층)
8. [바코드 파이프라인](#8-바코드-파이프라인)
9. [GS1 파싱 엔진](#9-gs1-파싱-엔진)
10. [검사 엔진 — 판정 로직](#10-검사-엔진--판정-로직)
11. [LOT 자동 매칭](#11-lot-자동-매칭)
12. [규격 자동 선택](#12-규격-자동-선택)
13. [보조 검증 계층](#13-보조-검증-계층)
14. [학습·교정 계층](#14-학습교정-계층)
15. [UI/UX 구현](#15-uiux-구현)
16. [데이터 임포트 / 익스포트](#16-데이터-임포트--익스포트)
17. [설정 시스템](#17-설정-시스템)
18. [견고성·오류 처리](#18-견고성오류-처리)
19. [빌드·배포·CI](#19-빌드배포ci)
20. [테스트 전략](#20-테스트-전략)
21. [성능 특성](#21-성능-특성)
22. [알려진 한계와 설계 트레이드오프](#22-알려진-한계와-설계-트레이드오프)
23. [확장 가이드](#23-확장-가이드)
- [부록 A. 설정 키 전체](#부록-a-설정-키-전체)
- [부록 B. 주요 수식 모음](#부록-b-주요-수식-모음)
- [부록 C. 용어집](#부록-c-용어집)

---

## 1. 목적과 배경

### 1.1 해결하려는 문제

의료기기 라벨은 **인쇄 시트(PDF)** 단위로 출력되며 한 페이지에 같은 LOT의 라벨이 여러 장
(규격에 따라 11~13장) 배치된다. 출하 전 검사자는 각 라벨의 LOT·PN·REF·제조일·유효기간·GTIN이
주문 목록과 일치하는지, 바코드가 올바른 값을 담는지 확인해야 한다.

| 문제 | 구체적 양상 | LaVIS의 대응 |
|---|---|---|
| **대조량 폭증** | 페이지당 필드 인스턴스 40~60개 × 수십 페이지 | 전 페이지 자동 OCR·대조, 불일치만 부각 |
| **누락 위험** | 한 장의 오기를 눈으로 놓치면 회수·규제 지적 | 필드별 **개수 검사**(13개 중 12개만 검출 → 확인 필요) |
| **도구 분리** | 목록 생성 프로그램과 검사 프로그램이 별개 | 한 앱 3탭으로 통합, 탭 간 직접 전달 |
| **OCR 오인식 반복** | O↔0, I↔1 혼동을 매번 사람이 판단 | 교정 사전·혼동 그룹 학습, 등록 즉시 영구 적용 |
| **기록 부재** | 결과가 화면에만 남음 | 주석 이미지 + SQLite 이력 + 24열 CSV |

### 1.2 설계 원칙

1. **거짓 합격 금지(Fail-safe)** — 판정 근거가 불완전하면 반드시 '확인 필요' 또는 오류로 드러낸다.
   LOT 미매칭, GS1 해석 불가, GTIN 추출 실패가 모두 여기에 해당한다.
2. **사람 우선(Human override)** — 사용자가 명시적으로 선택한 값(LOT·규격)은 모든 자동 추론보다 우선한다.
3. **재과금 금지** — 유료 OCR(AWS Textract) 호출은 캐시·중복 제출 차단·실패 시 프리페치 중단으로 방어한다.
4. **결정성** — 같은 입력·설정이면 같은 판정. 판정 서명(SHA-1) 해시로 검증한다.
5. **GUI 비의존 코어** — 판정 로직은 `LabelSuite.Core`(net8.0)에 있고 Linux CI에서 테스트된다.
6. **검사 보조 도구** — 자동 판정은 참고값이며 최종 판정 주체는 검사자다(§22.3).

### 1.3 규제상 위치

LaVIS는 **검사 보조 도구**다. 전자서명·감사추적(21 CFR Part 11) 대상 시스템이 아니며,
자매 제품 UDInspect와 같은 간이 검증 트랙(GAMP 기준 위험 낮음, 구성 가능한 도구)을 전제로 한다.
검증 근거는 ① 빌드 식별(정보 버전 + exe SHA-256) ② 자동 테스트 310건 ③ 실기 체크리스트
(`docs/manual_checklist.md` V-00~V-13)로 구성된다.

---

## 2. 시스템 개요

### 2.1 3계층 구조

```
┌──────────────────────────────────────────────────────────────┐
│ LabelSuite.App  (net8.0-windows, WPF)                        │
│  MainWindow(3탭 + 상태바) · InspectorView · GeneratorView     │
│  HistoryView · SettingsWindow · ResultListWindow · 대화상자    │
│  Services: PdfDoc · PrefetchWorker · Dialogs · UiFx ·          │
│            ExportGuard · InputRules · AppLog · SkiaWpf        │
└───────────────────────────┬──────────────────────────────────┘
                            │ (단방향 의존)
┌───────────────────────────▼──────────────────────────────────┐
│ LabelSuite.Core  (net8.0, GUI 비의존 — Linux 빌드·테스트 가능) │
│  입력: Schema · ListGenerator · AppConfig · Standards         │
│  이미지: ImagePreprocess                                      │
│  OCR:   IOcrEngine ← TextractClient / GlyphOcrEngine /        │
│         OnnxOcrEngine · OcrCache · OcrQuality · OcrCorrections│
│  바코드: BarcodeDetector · DataMatrixLocator ·                │
│         BarcodeBoxRefiner · BarcodeGrader · Gs1               │
│  판정:  InspectionEngine · InspectionSummary · SessionStats   │
│  보조:  StandardDetector · LabelFormDetector ·                │
│         SameValueChecker · LabelTypeProfiler · FieldCharsets  │
│  출력:  Annotate · InspectionCsv · CsvUtil · Report · HistoryDb│
│  이식:  PresetBundle · LearningBundle · PathRules             │
└──────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────┐
│ LabelSuite.Core.Tests  (xunit, 310건)                         │
└──────────────────────────────────────────────────────────────┘
```

App → Core 단방향 의존이며 Core는 WPF 타입을 전혀 참조하지 않는다. 이 규칙 덕분에 판정 로직
전체가 Linux CI에서 검증되고, Windows 빌드는 XAML 컴파일과 통합 확인만 담당한다.

### 2.2 전체 데이터 흐름

```
[엑셀 3종]                                    [라벨 PDF]
 주문일정/품목번호/BSC                              │
      │ ListGenerator.Generate                    │ PdfDoc.RenderPage (PDFium, dpi=72×zoom)
      ▼                                           ▼
 List<LabelRecord> ──────────────┐      ImagePreprocess.DeskewAndCropLiner
      │ Schema.SaveInspectionList │            (이형지 크롭 + 잉크 기준 기울기 보정)
      ▼                          │                ▼
 목록 xlsx ──Schema.Load──────────┤      ┌─────────┴──────────┐
                                  │      │ IOcrEngine         │ BarcodeDetector.Detect
                                  │      │ .DetectWordsAsync  │  ├ ZXing DecodeMultiple
                                  │      ▼                    │  └ DataMatrixLocator 전수
                                  │  List<OcrWord>            ▼
                                  │      └──────► PageAnalysis ──► OcrCache (SHA-1 키)
                                  │                   │
                                  │      ┌────────────┴──────────────┐
                                  │      ▼                           ▼
                                  │  MatchLot (§11)         StandardDetector (§12)
                                  │      ▼                           ▼
                                  └──► InspectionEngine.Inspect (§10)
                                         ├ WordMergeRules.Apply
                                         ├ OcrCorrections.Apply
                                         ├ FieldCharsets.Repair
                                         ├ CountField / GtinMatch / BarcodeGtinMatches
                                         ├ ApplyZones
                                         ├ BarcodeDetector.CrossCheckHits (Gs1.Parse)
                                         ├ MasterCheck.Check
                                         └ SameValueChecker.Check
                                         ▼
                                  InspectionOutcome (Passed, Fields, BarcodeChecks)
                                         │
                     ┌───────────────────┼────────────────────┬──────────────┐
                     ▼                   ▼                    ▼              ▼
              화면(배지·표·             Annotate           HistoryDb     InspectionCsv
              오버레이·모아보기)      .SaveAnnotatedJpeg  .RecordInspection  .Build
                     │                                        │
                     └── InspectorVerdict (검사자 확인 합격) ───┘
```

### 2.3 하드웨어·소프트웨어 요구사항

| 구분 | 요구사항 | 근거 |
|---|---|---|
| OS | Windows 10 build 19041+ / Windows 11, x64 | WPF + win-x64 self-contained 배포 |
| CPU | x64, 2코어 이상 권장 | OCR·바코드 분석이 백그라운드 1스레드 |
| 메모리 | 8GB 이상 권장 | 렌더 배율 4.0에서 A4 페이지 ≈ 2384×3370px ≈ 32MB/페이지, 캐시 6장 |
| 디스크 | 프로그램 1.2GB(자체 포함 + ONNX 모델), 데이터 폴더 수백 MB(OCR 캐시 500건) | `publish/LaVIS` 실측 |
| 네트워크 | AWS Textract 사용 시 HTTPS 아웃바운드. 로컬 엔진은 불필요 | `TextractClient` |
| 주변기기 | **없음** — 스캐너·카메라를 쓰지 않고 PDF 파일만 입력 | 스캐너 검사는 자매 제품 UDInspect 담당 |
| .NET 런타임 | **불필요** (self-contained 배포에 포함) | CI `--self-contained true` |

---

## 3. 기술 스택

### 3.1 런타임·프레임워크

| 항목 | 선택 | 이유 |
|---|---|---|
| 언어 | C# 12 (`LangVersion 12`) | 컬렉션 식(`[..]`), 기본 생성자, 패턴 매칭으로 코드량 감소 |
| 런타임 | .NET 8 (LTS) | 장기 지원, self-contained 단일 폴더 배포 |
| UI | WPF (`net8.0-windows`, `UseWPF`) | 벡터 기반 확대·오버레이 캔버스·DataGrid가 검사 UI에 적합 |
| Core TFM | `net8.0` | GUI 비의존 → Linux CI에서 전체 판정 로직 테스트 |
| 빌드 | `Nullable enable`, `ImplicitUsings enable`, `AllowUnsafeBlocks` | null 안전 + 픽셀 포인터 접근(§6.1) |

### 3.2 의존 패키지

| 패키지 | 버전 | 역할 | 대체 검토 |
|---|---|---|---|
| **PDFtoImage** | 5.2.1 | PDF → 비트맵 렌더 (PDFium 바인딩) | Ghostscript(라이선스), Docnet(유지보수) |
| **SkiaSharp** | 3.119.2 | 이미지 처리·오버레이 렌더·JPEG 인코딩 | System.Drawing(윈도우 전용, Linux 테스트 불가) |
| **SkiaSharp.NativeAssets.Linux.NoDependencies** | 3.119.2 | Linux CI에서 Skia 실행 | — |
| **ZXing.Net** | 0.16.11 | 바코드 디코딩 (순수 관리형) | ZBar(네이티브 의존), Dynamsoft(유료) |
| **ZXing.Net.Bindings.SkiaSharp** | 0.16.22 | SKBitmap ↔ ZXing 브리지 | — |
| **AWSSDK.Textract** | 3.7.400 | 클라우드 OCR (기본 엔진) | Azure/GCP OCR |
| **AWSSDK.SecurityToken** | 3.7.400 | 자격증명 사전 검증(`GetCallerIdentity`) | — |
| **RapidOcrNet** | 4.0.2 | 로컬 ONNX OCR (PP-OCRv5 Latin) | Tesseract(정확도·속도 열세) |
| **ClosedXML** | 0.104.2 | xlsx 읽기·쓰기 | EPPlus(라이선스 변경), OpenXML SDK(저수준) |
| **Microsoft.Data.Sqlite** | 8.0.11 | 검사 이력 DB | LiteDB, JSON 파일 |
| **xunit** | — | 테스트 프레임워크 | — |

> **버전 정합 주의** — PDFtoImage 5.2.1 · ZXing.Net.Bindings.SkiaSharp 0.16.22 · RapidOcrNet 4.0.2가
> 모두 SkiaSharp 3.119 세대를 요구한다. SkiaSharp를 올릴 때는 세 패키지를 함께 확인해야 한다
> (`LabelSuite.Core.csproj` 주석).

### 3.3 배포 형태

```bash
dotnet publish csharp/src/LabelSuite.App -c Release -r win-x64 \
  --self-contained true -p:InformationalVersion=<태그|sha7> -o publish/LaVIS
```

산출물 `LaVIS/` 폴더: `LaVIS.exe` + .NET 런타임 DLL + `models/`(ONNX) + `README.txt`.
사용자 데이터는 `%APPDATA%\LaVIS`에 **분리 저장**되므로 폴더를 새 버전으로 덮어써도 설정·학습·이력이 보존된다.

---

## 4. 프로젝트 구조와 파일별 책임

### 4.1 LabelSuite.Core (6,617행)

| 파일 | 행 | 책임 | 핵심 공개 API |
|---|---:|---|---|
| `Inspection.cs` | 513 | **검사 엔진** — 필드 카운트·판정·LOT 매칭·바코드 교차검증 | `InspectionEngine.Inspect`, `MatchLot`, `InspectionOutcome`, `BarcodeCrossCheck.Check` |
| `GlyphOcrEngine.cs` | 505 | 패턴 학습 OCR — 글리프 분할·템플릿 매칭·자동 학습 | `GlyphLibrary`, `GlyphOcrEngine.LearnFrom/DetectWordsAsync` |
| `BarcodeDetector.cs` | 449 | 바코드 검출·교차검증·클릭 판독·표 요약 | `Detect`, `DecodeAt`, `CrossCheckHits`, `Summarize`, `IsVerifiable` |
| `AppConfig.cs` | 430 | 설정 로드·마이그레이션·정규화·원자적 저장 | `Settings`, `Reload`, `SaveSettings`, `LearningEnabled`, `Corrections` |
| `HistoryDb.cs` | 359 | SQLite 이력·카운터·기준정보 DB | `RecordInspection`, `Query`, `NextFileCounter`, `MasterCheck.Check` |
| `ImagePreprocess.cs` | 278 | 휘도 버퍼·대비 스트레칭·이형지 크롭·기울기 보정 | `LumaBuffer`, `StretchContrast`, `DeskewAndCropLiner` |
| `DataMatrixLocator.cs` | 262 | DataMatrix 후보 탐지 (다중 격자·분리·L 파인더 정렬) | `FindCandidates`, `HasLFinder` |
| `SameValueCheck.cs` | 256 | 동일값 패턴 검사·드리프트 보정·기준 배치 학습 | `SameValueChecker.Check`, `ToCrossChecks` |
| `ListGenerator.cs` | 253 | 엑셀 3종 → 검사 목록 생성 | `Generate`, `ComputeExpDate`, `CleanRef` |
| `Annotate.cs` | 244 | 오버레이 렌더·요약 박스·결과 JPEG 저장·파일명 | `RenderOverlays`, `DrawBarcodeBoxes`, `SaveAnnotatedJpeg`, `MakeResultFilename` |
| `StandardDetector.cs` | 210 | 라벨 문서번호 → 규격 자동 판별 | `Detect`, `Signatures`, `Adjacent`, `Fuzzy` |
| `OcrCorrections.cs` | 189 | 교정 사전·혼동 문자 학습·정규형 | `Add`, `Apply`, `Canonicalize`, `ConfusableContains` |
| `LabelFormDetector.cs` | 187 | 양식 감지(텍스트 정규식 + 이미지 템플릿) | `Detect`, `LearnTemplate`, `ExtractThumb` |
| `PresetBundle.cs` | 187 | 프리셋 `.lavispreset` 내보내기/가져오기 | `Export`, `Inspect`, `Import` |
| `TextractClient.cs` | 186 | AWS Textract 호출·자격증명 검증·오류 한글화 | `DetectWordsAsync`, `ValidateCredentialsAsync`, `FriendlyCredentialError` |
| `OnnxOcrEngine.cs` | 174 | 로컬 ONNX OCR (RapidOCR PP-OCRv5 Latin) | `DetectWordsAsync` |
| `LabelTypeProfiler.cs` | 171 | 라벨 유형 학습·이상 알람 | `StaticTokens`, `Check`, `Learn`, `IsWordLike` |
| `Gs1.cs` | 169 | GS1 AI 파서(관대 모드)·GTIN 추출·날짜 | `Parse`, `TryExtractGtin`, `ParseDate`, `AiTable` |
| `BarcodeBoxRefiner.cs` | 159 | 검출점 → 실제 심볼 영역 확장 | `Refine` |
| `WordMergeRules.cs` | 157 | 단어 병합 학습·적용 | `Add`, `Apply` |
| `OcrCache.cs` | 150 | 페이지 분석 캐시(메모리 LRU + 디스크) · `BarcodeHit`/`PageAnalysis` 정의 | `PageKey`, `Get`, `Put` |
| `Schema.cs` | 138 | 검사 목록 계약·GTIN 정규화 | `LoadInspectionList`, `SaveInspectionList`, `NormalizeGtin14` |
| `SettingRanges.cs` | 136 | 숫자 설정 범위표(단일 출처) | `All`, `Find`, `RangeDef.Clamp/ParseInt` |
| `BarcodeGrader.cs` | 106 | 바코드 손상 간이 등급 A~F | `Grade` |
| `LearningBundle.cs` | 99 | 학습 데이터 `.lslearn` 이식 | `Export`, `Import` |
| `InspectionCsv.cs` | 93 | 24열 CSV 계약 | `Header`, `Row`, `Build` |
| `FieldCharsets.cs` | 86 | 필드 문자 제약·혼동 복원 | `Repair`, `Violations` |
| `Standards.cs` | 83 | 규격 정의 로드 | `StandardsBundle.Load`, `Spec`, `ChinaCodeForRef` |
| `FieldZoneStore.cs` | 73 | 영역 JSON 조작(교체/추가/카운트) | `Upsert`, `CountFor` |
| `SessionStats.cs` | ~50 | 세션 집계·다음 확인 대상 | `Of`, `NextAttention`, `ConfirmMessage` |
| `InspectionSummary.cs` | 33 | 판정 사유 한 줄 | `Describe` |
| `PathRules.cs` | ~60 | 경로 검증·파일명 정제·중복 회피 | `IsValidDirectory`, `SanitizeFileStem`, `UniquePath` |
| `CsvUtil.cs` | ~60 | CSV 파싱·엑셀 텍스트 보호 | `ParseLine`, `Unwrap`, `Field`, `TextField` |
| `OcrQuality.cs` | ~50 | 페이지 OCR 신뢰도 평가 | `Assess`, `LowConfidenceWords` |
| `Report.cs` | ~50 | LOT 리포트 xlsx | `ExportLotReport` |

### 4.2 LabelSuite.App (5,589행)

| 파일 | 행 | 책임 |
|---|---:|---|
| `Views/InspectorView.xaml(.cs)` | 2,954 | 검사 탭 전체 — PDF·OCR 오케스트레이션, 판정 표시, 오버레이, 모아보기, 자동 검사, 검사자 처리, 저장 |
| `SettingsWindow.xaml(.cs)` | 734 | 5탭 설정 창 — 로드/검증/저장, 프리셋, 백업 |
| `Views/GeneratorView.xaml(.cs)` | 338 | 목록 생성 탭 — 입력 3종, 날짜 트리, 미리보기, 이슈 패널 |
| `MainWindow.xaml(.cs)` | 233 | 3탭 셸, 상태바, About 오버레이, 키 라우팅, 종료 확인 |
| `OcrLogWindow.xaml(.cs)` | 156 | OCR 인식 로그 — 필터·유사도 정렬·병합/교정 등록 |
| `Views/HistoryView.xaml(.cs)` | 123 | 이력 조회·필터·리포트·기준정보 DB |
| `Services/PdfDoc.cs` | 118 | PDF 래퍼 — LRU 렌더 캐시, 소유 렌더 |
| `Services/PrefetchWorker.cs` | 109 | 우선순위 큐 + 세대 태깅 백그라운드 분석 |
| `App.xaml.cs` | 108 | 단일 인스턴스 뮤텍스, 전역 예외 → crash.log, 버전 |
| `ResultListWindow.xaml(.cs)` | 99 | 결과 목록(비모달) — 부적합 필터, 이동, 합격 처리 |
| `Services/InputRules.cs` | 96 | 숫자 입력 검증 배선(범위표 연동) |
| `Services/UiFx.cs` | 82 | 행·셀·도형 앰버 플래시 애니메이션 |
| `Services/ExportGuard.cs` | 71 | 파일 쓰기 예외 → 원인별 한글 안내 |
| `InputDialog.cs` / `ChoiceDialog.cs` / `Services/Dialogs.cs` | 179 | 공용 대화상자 규범(기본 '아니오', Esc, 동작 라벨) |
| `MasterDbWindow.xaml(.cs)` | 59 | 기준정보 DB 편집 |
| `CorrectionDialog.xaml(.cs)` | 48 | 교정 등록 |
| `Services/SkiaWpf.cs` | 39 | SKBitmap → WPF BitmapSource |
| `Theme.xaml` | — | 디자인 토큰·스타일(패밀리 디자인) |

---

## 5. 데이터 모델

### 5.1 핵심 레코드

```csharp
// 검사 목록 한 행 = 기대값의 원천
public sealed record LabelRecord(
    string Lot, string Products, string Pn, string Ref,
    string MfgDate, string ExpDate, string Gtin, string? Standard = null);

// OCR이 읽은 단어 하나 (좌표는 렌더 이미지 픽셀 기준)
public sealed record OcrWord(string Text, (int X, int Y, int W, int H) Bbox, int Confidence);

// 검출된 바코드 하나
public sealed record BarcodeHit(
    string Symbology, string Text, (int X, int Y, int W, int H) Bbox, bool IsGs1,
    string? Grade = null)
{
    [JsonIgnore]
    public bool IsDataMatrix =>
        Symbology.Contains("DataMatrix", StringComparison.OrdinalIgnoreCase);
}

// 페이지 1장의 분석 결과 (캐시 단위)
public sealed class PageAnalysis
{
    public List<OcrWord> Words { get; init; } = [];
    public List<BarcodeHit> Barcodes { get; init; } = [];
}
```

### 5.2 판정 모델

```csharp
public sealed class FieldResult
{
    public required string Field { get; init; }      // "LOT", "GTIN", 커스텀 이름
    public required string Term { get; init; }       // 이 페이지에서 찾을 값
    public int? Expected { get; init; }              // 규격이 요구하는 횟수
    public List<TextMatch> Matches { get; init; } = [];
    public int Found => Matches.Count;

    public bool Gating => Expected is > 0 && Term.Length > 0;   // 판정에 영향을 주는가
    public bool AtLeastOne { get; init; }                        // GTIN 전용: ≥1이면 합격
    public bool Passed => !Gating || (AtLeastOne ? Found >= 1 : Found == Expected);
    public string ExpectedDisplay => AtLeastOne ? "≥1"
                                   : Expected is { } e ? e.ToString() : "-";

    public string Source { get; init; } = "OCR";        // "바코드" | "OCR" | "없음"
    public bool ExtractionFailed { get; init; }          // 바코드·OCR 모두 실패(오류)
}

public sealed record CrossCheckResult(
    string Source, string Field, string BarcodeValue, string ExpectedValue, bool Matched);

public sealed class InspectionOutcome
{
    public required LabelRecord Record { get; init; }
    public required StandardSpec Standard { get; init; }
    public Dictionary<string, FieldResult> Fields { get; } = [];
    public List<CrossCheckResult> BarcodeChecks { get; init; } = [];
    public string SearchTerm { get; init; } = "";

    public bool Passed => Fields.Values.All(f => f.Passed)
                       && BarcodeChecks.All(c => c.Matched);
}

// 검사자 최종 처리 — 자동 판정은 보존되고 최종 판정만 바뀐다
public sealed record InspectorVerdict(bool Passed, string By, DateTime At, string Note);
```

### 5.3 규격 정의 (`standards.json`)

```json
{
  "standards": {
    "MDR": {
      "counts": { "LOT": 11, "PN": 3, "REF": 11, "MFG DATE": 2,
                  "EXP DATE": 3, "GTIN": 8, "CHINA": 0 },
      "date_format": "%Y-%m-%d",
      "uses_china_field": false,
      "display_name": "PML-001(Rev.1)",
      "doc_patterns": ["FORM-\\d{3}"]
    }
  },
  "china_ref_mapping": { "HEV": "LBDA-02", "NDS": "LBDC-01" },
  "field_colors": { "LOT": [255,0,0,100], "GTIN": [0,255,255,100] }
}
```

| 키 | 의미 | 비고 |
|---|---|---|
| `counts.<필드>` | 기대 출현 횟수. 0 = 검사 안 함 | **`GTIN` 값은 무시** — 항상 ≥1 규칙(§10.4) |
| `date_format` | strftime → `.NET` 변환(`%Y`→`yyyy`) | BSC만 `%Y.%m.%d` |
| `uses_china_field` | CHINA 필드 활성(REF 접두 3자 → 코드) | |
| `display_name` | UI 표시명 **이자 문서번호 감지 서명의 원천**(§12.1) | 내부 키는 불변 |
| `doc_patterns` | 문서번호 감지 보강 정규식(최우선) | 선택 |

기본 6규격: `MDR`=PML-001(Rev.1), `MDD`=PML-001(Rev.0), `BSC`=BSL-01(Rev.5),
`A00`, `A02`, `중국`.

### 5.4 SQLite 스키마

```sql
CREATE TABLE inspections (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts TEXT NOT NULL,                    -- yyyy-MM-ddTHH:mm:ss
  lot TEXT, ref TEXT, pn TEXT, products TEXT, gtin TEXT,
  standard TEXT, source TEXT,          -- source: "pdf"
  pdf_path TEXT, page INTEGER,         -- page: 0-based
  passed INTEGER NOT NULL,             -- 자동 판정 (보존)
  image_path TEXT,
  app_version TEXT,                    -- CI 정보 버전 (도구 식별)
  inspector_verdict TEXT,              -- NULL | 'PASS' | 'FAIL'
  inspector_note TEXT, inspector_by TEXT
);
CREATE TABLE inspection_fields (
  inspection_id INTEGER NOT NULL REFERENCES inspections(id) ON DELETE CASCADE,
  field TEXT NOT NULL, expected INTEGER, found INTEGER, passed INTEGER);
CREATE TABLE barcode_checks (
  inspection_id INTEGER NOT NULL REFERENCES inspections(id) ON DELETE CASCADE,
  source TEXT, field TEXT, barcode_value TEXT, expected_value TEXT, matched INTEGER);
CREATE TABLE counters (name TEXT PRIMARY KEY, value INTEGER NOT NULL);
CREATE TABLE label_master (            -- 기준정보 DB
  pn TEXT PRIMARY KEY, products TEXT, ref TEXT, gtin TEXT,
  expected_symbologies TEXT,           -- 'GS1 DataMatrix,GS1-128'
  note TEXT, updated_at TEXT);
CREATE INDEX idx_inspections_lot ON inspections(lot);
CREATE INDEX idx_inspections_ts  ON inspections(ts);
```

`PRAGMA journal_mode=WAL`(동시 읽기 중 쓰기 허용), `PRAGMA foreign_keys=ON`(CASCADE 삭제).
구버전 DB 호환은 **열 존재 검사 후 ALTER**로 처리한다.

```csharp
foreach (var column in new[] { "inspector_verdict", "inspector_note", "inspector_by" })
    if (!HasColumn("inspections", column))
        Execute($"ALTER TABLE inspections ADD COLUMN {column} TEXT");
```

### 5.5 최종 판정 파생

```csharp
public bool FinalPassed =>
    InspectorVerdict == "PASS" || (InspectorVerdict.Length == 0 && Passed);
```

즉 **최종 = 검사자 처리가 있으면 그것, 없으면 자동 판정**이며 자동 판정 컬럼은 절대 덮어쓰지 않는다.

---

## 6. 이미지 파이프라인

**파일**: `ImagePreprocess.cs`, `Services/PdfDoc.cs`

### 6.1 PDF 렌더

PDFium(`PDFtoImage`)으로 페이지를 비트맵으로 변환한다. 배율 → DPI 변환:

```
dpi = round(72 × pdf_render_zoom)      // 기본 zoom=4.0 → 288 dpi
```

두 가지 렌더 경로가 있고 **소유권 계약**이 다르다.

| 메서드 | 반환 비트맵 소유 | 용도 |
|---|---|---|
| `RenderPage(index)` | **캐시 소유** — 호출자는 해제 금지 | UI 표시 (LRU `page_image_cache_pages`, 기본 6) |
| `RenderPageOwned(index)` | **호출자 소유** — 반드시 Dispose | 백그라운드 분석 |

분석 스레드가 공유 캐시를 쓰면 LRU 축출이 분석 도중 비트맵을 해제해 **좌표 오염·크래시**가 발생한다.
이 때문에 워커는 항상 소유본을 받고 분석 후 `finally`에서 해제한다.

```csharp
var image = job.Render();                       // 소유 비트맵 계약
try { analysis = await _analyze(image); }
finally { image.Dispose(); }
```

초대형 렌더 방어: 비트맵이 60MB를 넘으면 캐시 한도를 6 → 2로 낮춘다.

```csharp
var limit = bitmap.ByteCount > 60_000_000 ? 2 : CachePages;
```

### 6.2 휘도 버퍼 (모든 화소 연산의 기반)

ITU-R BT.601 계수의 정수 근사:

```
Y = (299·R + 587·G + 114·B) / 1000
```

BGRA8888/RGBA8888이면 `unsafe` 포인터로 행 단위 접근해 `GetPixel` 대비 수십 배 빠르다.
그 외 색 형식은 `GetPixel` 폴백. 수천만 화소 이미지에서 이 최적화가 전체 성능을 좌우한다.

### 6.3 이진화 문턱 (공통 규칙)

`DataMatrixLocator`, `BarcodeBoxRefiner`, `GlyphOcrEngine`, `BarcodeGrader`가 모두 같은 규칙을 쓴다.

```
히스토그램 H[0..255]
lo = min{ v : Σ(H[0..v]) ≥ 0.02·N }      // 2% 퍼센타일
hi = max{ v : Σ(H[v..255]) ≥ 0.02·N }    // 98% 퍼센타일
threshold = (lo + hi) / 2
```

극단값 2%를 잘라 먼지·반사광에 흔들리지 않게 한 뒤 중간값을 취한다. 라벨은 "흰 바탕 + 검정 잉크"
분포라 이 단순 규칙이 Otsu보다 안정적이면서 훨씬 빠르다.

### 6.4 대비 스트레칭 (`StretchContrast`)

```
out(x,y) = clamp( (Y(x,y) − lo) × 255 / (hi − lo), 0, 255 )
조기 반환: hi ≤ lo + 5  →  원본 사본 (이미 대비 충분 또는 균일 이미지)
```

흐린 인쇄에 효과적이며 `ocr.contrast_stretch`로 켠다. **분석에만 적용**되고 화면 표시는 원본이다.

### 6.5 라벨 정렬 — 이형지 크롭 + 기울기 보정 (`DeskewAndCropLiner`)

라벨 시트는 하늘색 이형지(liner) 위에 인쇄된다. 이형지 영역을 찾아 잘라내면 페이지마다 라벨의
위치·크기가 일정해져 **영역 등록(zone)과 양식 감지가 좌표 기반으로 동작**할 수 있다.

**① 이형지 픽셀 판정** (서브샘플링 간격 2px)

```
하늘색(x,y)  ⟺  B ≥ 110  ∧  B > R + 18  ∧  G + 6 ≥ R
```

파랑이 충분히 밝고 빨강보다 뚜렷이 크며 초록이 빨강 이상 — 흰 배경(R≈G≈B)과 검정 잉크를 모두 배제한다.
하늘색 픽셀 수가 샘플의 2% 미만이면 **미감지**로 보고 원본을 그대로 반환한다(`Cropped=false`).

**② 기울기 추정** — 이형지가 아니라 **본 라벨의 잉크**(텍스트 행·수평선) 기준

잉크 화소 집합 `P = {(xᵢ,yᵢ) : Y(xᵢ,yᵢ) < 115}`를 모으고(샘플 300개 미만이면 0도 반환),
각도 θ로 회전했을 때의 **가로 투영 선명도**를 점수로 쓴다.

```
proj_i(θ) = round( (xᵢ·sin θ + yᵢ·cos θ) / 3 )        // 3px 빈
Score(θ)  = Σ_b ( count(b) )²                          // 빈별 화소 수의 제곱합
```

텍스트 행이 수평일 때 화소가 소수의 빈에 몰려 제곱합이 최대가 된다(분산 최대화 원리).
탐색은 **거친 → 미세 2단계**:

```
1차: θ ∈ [−6°, +6°], 0.5° 간격
2차: θ ∈ [best−0.4°, best+0.4°], 0.1° 간격
채택 조건: Score(best) > Score(0) × 1.03      // 3% 이상 선명해질 때만
적용 조건: 0.3° ≤ |θ| ≤ 10°                   // 미세·과대 회전 배제
```

`×1.03` 문턱이 없으면 노이즈에도 회전이 걸려 좌표가 흔들린다. `≤10°`는 오검출 방어다.

**③ 크롭** — 회전 후 이형지 경계를 다시 스캔하고 여백을 붙여 잘라낸다.

```
margin = max(4, min(W, H) / 200)
crop   = [minX−margin, minY−margin] ~ [maxX+margin, maxY+margin]
결과가 20×20px 미만이면 원본 반환 (오검출 방어)
```

**④ 캐시 서명** — 정렬 결과는 OCR 캐시 키에 포함된다.

```csharp
private string PreprocessSig =>
    _config?.SectionBool("preprocess", "crop_label", true) == true ? "crop-v2" : "none-v1";
```

> **⚠ 유지보수 주의** — 정렬 알고리즘을 고치면 **반드시 `crop-v2` → `crop-v3`으로 올려야 한다.**
> 이전 알고리즘의 크롭 기준으로 저장된 OCR 좌표가 재사용되면 박스가 통째로 어긋난다.

---

## 7. OCR 엔진 계층

### 7.1 공통 인터페이스

```csharp
public interface IOcrEngine
{
    string Id { get; }                  // "aws" | "pattern" | "onnx"
    string DisplayName { get; }
    Task<List<OcrWord>> DetectWordsAsync(SKBitmap image, CancellationToken ct = default);
}
```

**실패를 빈 결과로 위장하지 않는다** — 모든 엔진은 실패 시 `OcrException`을 던진다.
빈 리스트를 반환하면 "글자가 없는 페이지"와 구분되지 않아 거짓 합격으로 이어진다.

| 엔진 | Id | 네트워크 | 과금 | 정확도 | 사용 시점 |
|---|---|---|---|---|---|
| AWS Textract | `aws` | 필요 | 페이지당 | 최상 | 기본 |
| 패턴 학습 | `pattern` | 불필요 | 없음 | 학습량 의존 | 오프라인·학습 축적 후 |
| ONNX RapidOCR | `onnx` | 불필요 | 없음 | 중간 | 오프라인 |

### 7.2 AWS Textract (`TextractClient`)

**전송 전 처리**

```
maxDimension 초과 시 축소 (Mitchell 큐빅 리샘플)
  scale = min(maxDim/W, maxDim/H)
JPEG 인코딩 (품질 ocr.jpeg_quality, 기본 85)
```

**좌표 환산** — Textract는 정규화 좌표(0~1)를 주므로 픽셀로 되돌린다.

```
X = round(Left × imageWidth),  Y = round(Top × imageHeight)
W = max(1, round(Width × imageWidth)),  H = max(1, round(Height × imageHeight))
```

**후처리 필터**

```csharp
var text = block.Text.Trim()
    .Replace('—', '-').Replace('–', '-').Replace("©", "(C)");
// 단독 비-GTIN AI 조각 제외 — (01) 포함 문자열은 GTIN 검사용으로 유지
if (!text.Contains("(01)") && NonGtinAi.IsMatch(text)) continue;   // (17)(10)(240)(30)(21)
```

`(17)`·`(10)` 같은 AI 표기 조각이 REF·LOT 매칭에 섞여 오검출을 만드는 것을 막는다.

**자격증명 사전 검증** — Textract 호출 전에 STS `GetCallerIdentity`(5초 타임아웃)로 확인하고,
SDK 원문 오류를 초보자가 조치 가능한 한국어로 변환한다.

| 원문 패턴 | 변환된 안내 |
|---|---|
| `EC2 Instance Metadata` / `Unable to find credentials` | credentials 파일 존재 여부에 따라 4단계 점검(확장자·`[default]`·BOM·재시작) |
| `InvalidClientTokenId` | "액세스 키가 잘못되었습니다 (오타 또는 비활성화된 키)" |
| `SignatureDoesNotMatch` | "Secret Key가 잘못되었습니다 (복사 시 일부 누락)" |
| 타임아웃 / `NameResolutionFailure` | "AWS에 연결할 수 없습니다. 인터넷/방화벽/프록시를 확인하세요" |

### 7.3 패턴 학습 OCR (`GlyphOcrEngine`) — 핵심 알고리즘

**원리**: 라벨 폰트는 고정이고 PDF 렌더는 결정적이므로, 신뢰할 수 있는 OCR(Textract) 결과에서
문자 이미지를 잘라 "문자 → 템플릿" 사전을 축적하면 이후 로컬에서 **정규화 상관계수(NCC)**로 복원할 수 있다.

#### 7.3.1 템플릿 표현

```csharp
public sealed record GlyphTemplate(float[] Vector, float Aspect,
                                   float VOffset = 0.5f, float RelHeight = 1f);
```

| 특징 | 정의 | 목적 |
|---|---|---|
| `Vector` | 20×28 = 560차원, zero-mean unit-norm | 모양 |
| `Aspect` | `glyph.W / glyph.H` | 종횡비 다른 글자 배제 |
| `VOffset` | `(glyph.Y + glyph.H/2 − row.Y) / row.H` ∈ [0,1] | `'`(상단)·`,`(하단)·`-`(중단) 구분 |
| `RelHeight` | `min(1, glyph.H / row.H)` | 소형 특수문자 vs 일반 글자 구분 |

#### 7.3.2 정규화 (수식)

```
1) 픽셀 강도:  ink(x,y) = clamp( (T×1.3 − Y(x,y)) / (T×1.3), 0, 1 )   // T = 이진화 문턱
   → 이진값이 아닌 연속값을 써서 획 굵기·곡률 정보를 보존
2) 종횡비 보존 리샘플:  scale = min(20/W, 28/H), 20×28 캔버스 중앙 배치
3) zero-mean:   c_i = p_i − mean(p)
4) unit-norm:   v_i = c_i / ‖c‖₂          (‖c‖₂ < 1e-6이면 c 그대로)
```

정규화 결과 **NCC = 단순 내적**이 된다.

```
NCC(a, b) = Σ aᵢ·bᵢ ∈ [−1, 1]
```

```csharp
internal static double Ncc(float[] a, float[] b)
{
    double dot = 0;
    for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
    return dot;   // zero-mean/unit-norm이므로 내적 = 상관계수
}
```

#### 7.3.3 매칭 (배제 조건 → 최대 NCC)

```
후보 c, 템플릿 t에 대해
  aspectRatio = max(c.Aspect, t.Aspect) / max(0.01, min(c.Aspect, t.Aspect))
  배제 ① aspectRatio > 1.8
  heightRatio = max(c.RelHeight, t.RelHeight) / max(0.05, min(...))
  배제 ② heightRatio > 1.9
  배제 ③ 둘 다 소형(RelHeight < 0.55) ∧ |c.VOffset − t.VOffset| > 0.28
  점수 = NCC(t.Vector, c.Vector)
결과 = argmax 점수, 점수 < MinScore(0.55)이면 '?'
```

#### 7.3.4 학습 (`LearnFrom`)

```
조건: word.Confidence ≥ 90 (신뢰 학습원만)
1) 단어 박스 안에서 세로 잉크 프로젝션으로 글리프 분할
2) 분할 수 > 글자 수이면 MergeNearestPairs로 인접 쌍 병합 (™·@ 등 획 분리 글자)
3) 분할 수 == 글자 수일 때만 학습 (불일치 시 전체 보류 — 잘못된 대응 방지)
4) 문자별 최대 6개 템플릿, NCC > 0.985면 중복으로 무시, 초과 시 가장 오래된 것 제거
```

#### 7.3.5 인식 (`Detect`)

```
1) SegmentRows: 가로 잉크 프로젝션 → 높이 5px 이상 행
2) SegmentGlyphs: 행 안에서 세로 잉크 프로젝션 → 폭 2px 이상 글리프
3) 단어 분리 문턱:
     medianWidth = 글리프 폭 중앙값
     gapThreshold = max(3, medianWidth × 0.6)
     narrowLimit  = max(2, medianWidth / 2)
     좁은 글리프(-·.·') 주변은 splitThreshold = max(gapThreshold, medianWidth)  ← 더 큰 간격에서만 분리
4) 저신뢰(< 0.55) 글리프는 다음 글리프와 병합 재시도
     조건: 0 ≤ gap ≤ medianWidth/4  ∧  mergedWidth ≤ medianWidth × 1.6
5) 단어 확정 조건: 인식된 글자 수 ≥ max(1, 전체/2)
   신뢰도 = 평균 NCC × 100
```

**가드**: 라이브러리에 문자가 8종 미만이면 `OcrException`으로 학습 유도 메시지를 띄운다.

```csharp
if (Library.CharCount < 8)
    throw new OcrException(
        "패턴 라이브러리가 아직 비어 있습니다.\n" +
        "AWS Textract 엔진으로 몇 페이지 검사하면 글자 패턴이 자동 학습되고,\n" +
        "그 후 이 엔진을 사용할 수 있습니다.");
```

#### 7.3.6 영속 형식

`glyphs.json` — 문자 → base64 float 블롭 배열. 블롭 레이아웃:

```
[Aspect][VOffset][RelHeight][Vector 560개]   총 563 float = 2,252 byte
구버전 호환: 길이가 561이면 [Aspect][Vector]로 읽고 위치 특징은 기본값
```

### 7.4 ONNX 로컬 엔진 (`OnnxOcrEngine`)

RapidOCR PP-OCRv5 Latin 모델(검출 + 각도 분류 + 인식)을 OnnxRuntime으로 실행한다.
모델 경로는 `AppContext.BaseDirectory` 기준 절대 경로로 재지정해 작업 폴더와 무관하게 동작한다.

```csharp
static string Rebase(string path) => Path.IsPathRooted(path)
    ? path : Path.Combine(AppContext.BaseDirectory, path);
set = set with { DetModelPath = Rebase(set.DetModelPath), /* Cls, Rec, Keys 동일 */ };
```

### 7.5 분석 캐시 (`OcrCache`)

**키 생성** — 결과의 유효성을 좌우하는 모든 입력을 해시한다.

```
raw = "{docPath}|{mtime:F3}|{page}|{renderZoom:F2}|{preprocessSig}"
key = SHA1(raw) 소문자 hex (40자)
preprocessSig = "crop-v2|eng=aws|cs=False"
```

문서 경로·수정시각·페이지·배율·전처리·엔진·대비 보정이 모두 키에 들어가므로 **어느 하나만 바뀌어도
다른 캐시**가 된다. 같은 PDF를 다시 열면 재과금이 없다.

**2단 캐시**: 메모리 LRU(`ocr_cache_max_entries`, 기본 500) + 디스크 JSON.
디스크는 `Put` 때마다 `LastWriteTimeUtc` 오름차순으로 초과분을 삭제한다.

### 7.6 프리페치 워커 (`PrefetchWorker`)

```csharp
private sealed record Job(int Priority, long Sequence, int Page, int Generation,
                          string CacheKey, Func<SKBitmap> Render);
```

| 장치 | 동작 | 목적 |
|---|---|---|
| **우선순위** | `OrderBy(Priority).ThenBy(Sequence)` — 현재 페이지 0, 프리페치 1 | 사용자가 보는 페이지 우선 |
| **세대(Generation)** | 문서 교체·설정 변경 시 `NewGeneration()`으로 증가, 큐·대기 집합 클리어 | 이전 문서 잔여 잡 폐기 |
| **중복 차단** | `HashSet<(Generation, Page)>`에 이미 있으면 제출 무시 | **Textract 이중 과금 차단** |
| **결과 저장** | `PageDone`은 세대와 무관하게 캐시에 먼저 저장 | 이미 과금된 결과는 버리지 않음 |
| **소유 렌더** | `job.Render()` 결과를 `finally`에서 Dispose | 캐시 축출 경합 방지 |

```csharp
_worker.PageDone += (gen, page, key, analysis) => { ... };
private void OnPageDone(int generation, int page, string key, PageAnalysis analysis)
{
    _cache.Put(key, analysis);              // 과금된 결과는 세대와 무관하게 저장
    if (generation != _worker.Generation) return;
    _analyses[page] = analysis;
    ...
}
```

### 7.7 OCR 품질 평가 (`OcrQuality`)

```
average  = mean(confidence)
lowCount = |{ w : w.Confidence < lowWordThreshold }|      // 기본 70
lowRatio = lowCount / wordCount
IsPoor   = average < minAverage (기본 80)  ∨  lowRatio > 0.3
```

`IsPoor`이면 상태바 경고 + 배지 사유줄에 `⚠ OCR 신뢰도 n%` 추가.
저신뢰 단어 하이라이트는 **검출 필드와 겹치는 단어에만** 그린다(§15.4).

---

## 8. 바코드 파이프라인

**파일**: `BarcodeDetector.cs`, `DataMatrixLocator.cs`, `BarcodeBoxRefiner.cs`, `BarcodeGrader.cs`

### 8.1 검출 전략

ZXing의 전체 페이지 `DecodeMultiple`은 큰 이미지 속 작은 DataMatrix를 자주 놓치는 알려진 약점이 있다.
LaVIS는 **두 경로를 합집합**으로 쓴다.

```csharp
public static List<BarcodeHit> Detect(SKBitmap image)
{
    // 경로 A: 전체 페이지 DecodeMultiple (1D·QR에 효과적)
    var results = Reader.DecodeMultiple(image);
    // DataMatrix 결과는 보충용으로 분리 보관
    ...
    // 경로 B: DataMatrix 전수 탐지 (로케이터 → 크롭 집중 디코딩)
    var dataMatrixHits = DetectDataMatrixByLocator(image);
    foreach (var fallback in dmFromMultiple)
        if (!dataMatrixHits.Any(h => Overlaps(h.Bbox, fallback.Bbox)))
            dataMatrixHits.Add(fallback);
    hits.AddRange(dataMatrixHits);
    return hits;
}
```

**ZXing 설정의 두 가지 결정적 선택**

```csharp
AutoRotate = false,   // ① 회전 시도 성공 시 '회전된 이미지' 좌표가 반환되어 박스가 엉뚱한 곳에 그려짐
Options = new DecodingOptions {
    TryHarder = true, TryInverted = true,
    AssumeGS1 = true, // ② 선두 FNC1 → ']C1', 이후 FNC1 → GS(0x1D) 복원
    PossibleFormats = [DATA_MATRIX, QR_CODE, CODE_128, CODE_39, EAN_13, ITF],
}
```

`AssumeGS1 = true`가 없으면 FNC1이 통째로 버려져 가변장 `(10)` LOT 값이 뒤따르는 `(17)` 유효기한까지
삼켜 **LOT 불일치로 오판**한다. 이 옵션이 §9.5 FNC1 복원과 함께 GS1-128 판정 정확도의 핵심이다.

### 8.2 DataMatrix 후보 탐지 (`DataMatrixLocator`)

#### 8.2.1 다중 격자

세 가지 (셀 크기, 밀도 문턱) 조합으로 각각 탐지하고 결과를 합친다.

| 격자 | 셀 | 밀도 문턱 | 최대 변 | 노리는 대상 |
|---|---|---|---|---|
| 표준 | 8px | 0.28 | 700px | 일반 크기 심볼 |
| 고밀도 | 8px | 0.42 | 700px | 텍스트보다 촘촘한 모듈만 남겨 텍스트 덩어리와 분리 |
| 세밀 | 4px | 0.30 | 240px | 소형 심볼(20~60px) |

```
셀 (gx,gy)가 'dark' ⟺  (문턱 미만 화소 수 / 샘플 수) ≥ DarkRatio
샘플 간격: 셀 8px → 2px, 셀 4px → 1px
```

#### 8.2.2 연결 요소 → 정방형 필터

4방향 BFS(좌우 이웃은 같은 행일 때만)로 dark 셀을 묶고 바운딩 박스를 구한다.

```
aspect = cellsW / cellsH
fill   = |component| / (cellsW × cellsH)
정상 후보 조건:  0.55 ≤ aspect ≤ 1.8  ∧  fill ≥ 0.45
                ∧ minSideCells ≤ cellsW, cellsH ≤ maxSideCells
점수 Score = fill × min(aspect, 1/aspect)          // 정방형·밀집일수록 1에 근접
```

#### 8.2.3 맞닿은 요소 분리 (`SplitSquares`)

테두리 선·텍스트와 이어져 길쭉해지거나 성긴 연결 요소에서 **정방형 고밀도 부분 창**을 찾는다.
누적합(summed-area table)으로 O(1) 면적 조회.

```
sum[y][x] = mask 누적합
Area(x,y,s) = sum[y+s][x+s] − sum[y][x+s] − sum[y+s][x] + sum[y][x]
fill = Area(x,y,s) / s²
채택: fill ≥ 0.8
점수 = fill × (1 − RingDark(x,y,s))
       RingDark = 창 둘레 1칸 띠의 dark 비율
```

**RingDark의 의미** — DataMatrix는 붙은 쪽을 빼면 둘레가 밝다(콰이엇 존). 텍스트 덩어리 속 창은
둘레가 계속 어둡다. 이 항이 진짜 심볼과 텍스트 조각을 가른다.

창 크기는 짧은 변부터 0.7배씩 4단계, 요소당 최대 4개(겹침 30% 이상 제거).
짧은 변이 32px 미만인 요소(보통 텍스트 한 줄)는 건너뛴다 — 그보다 작은 심볼은 모듈이 2px 미만이라
어차피 디코드되지 않는다.

#### 8.2.4 L 파인더 우선 정렬

DataMatrix 고유 특징인 **인접한 두 변의 실선(L 파인더)** 유무로 후보 순위를 매긴다.

```
band = clamp(min(W,H)/4, 4, 12)
RowDark(y) = (행 y에서 문턱 미만 화소 수) / 폭
ColDark(x) = (열 x에서 문턱 미만 화소 수) / 높이
top/bottom/left/right = 각 변 ±band 범위의 최대 밀도
HasLFinder ⟺ (left ≥ 0.85 ∨ right ≥ 0.85) ∧ (top ≥ 0.85 ∨ bottom ≥ 0.85)
```

최종 정렬: `HasLFinder 내림차순 → Split 오름차순(원형 우선) → Score 내림차순`,
겹침 50% 이상 제거, 최대 48개.

### 8.3 크롭 집중 디코딩 — 4단계 폴백

후보 하나를 디코드할 때 순서대로 시도하고 성공하면 즉시 반환한다.

| 단계 | 방법 | 노리는 상황 |
|---|---|---|
| ① | 원본 크롭, **4방향 회전** 시도 | 일반 |
| ② | **L 유도 정확 크롭** — 실선 L로 구한 정사각형 바깥을 흰색으로 | 후보가 텍스트·선과 붙거나 격자 때문에 심볼을 조금 잘라낸 경우 |
| ③ | 후보를 한 격자 칸(8px) 확장 후 바깥 흰색(콰이엇 존 합성) | 맞닿은 객체 제거 |
| ④ | 안쪽으로 `max(2, size/16)` 축소 후 흰색 | 얇은 테두리 선이 파고든 경우 |

소형 심볼은 확대해 디코딩 성공률을 올린다: `size < 50 → 3배`, `< 90 → 2배`, 그 외 1배.

**L 유도 정사각형(`LGuidedSquare`)** — 후보 주변에서 실선(1px 틈 허용) 런을 찾아 모서리에서 만나는
길이가 비슷한 행·열 쌍을 고른다.

```
minLen = min(rect.W, rect.H) × 0.6,  maxLen = max(rect.W, rect.H) × 1.7
agreement = |rowLen − colLen| / max(rowLen, colLen)
채택 조건: agreement ≤ 0.15
         ∧ 열 위치가 행 구간 한쪽 끝 ±4px
         ∧ 행 위치가 열 구간 한쪽 끝 ±4px
         ∧ 후보 사각형과 겹침
결과: argmin agreement 의 side × side 정사각형
```

프레임 선은 길이가 심볼과 맞지 않아 `agreement` 조건에서 자연히 배제된다.

### 8.4 바운딩 박스 정밀화 (`BarcodeBoxRefiner`)

ZXing이 반환하는 `ResultPoints`는 파인더/시작·정지 패턴의 점 몇 개일 뿐이라 그대로 쓰면 박스가 어긋난다.

**1D (GS1-128 등)** — 세로 막대 런 분석

```
1) 시드 스캔라인 yCenter ±2px에서 어두운 열을 막대 후보로
2) 각 열에서 위/아래로 연속 어두운 구간 확장 → (top, bottom)
3) 길이 ≥ 15px인 막대만 채택, 8개 이상일 때만 진행
4) top/bottom의 중앙값으로 밴드 결정
5) 밴드를 60% 이상 덮는 막대만 심볼로 인정 (주변 텍스트·잡티 배제)
6) 결과 = (min X, top) ~ (max X, bottom)
실패 시 시드 유지
```

**2D (DataMatrix/QR)** — 밀도 확장. L 보더 덕에 심볼 안 모든 행/열에 어두운 화소가 있다.

```
Expand: 밀도 ≥ 0.12인 동안 확장, 연속 4줄(window) 비면 정지 후 되감기
2회 반복 (가로 → 세로 → 가로 → 세로)
cap = maxGrowth 지정 시 size + max(4, maxGrowth), 아니면 size×2 + 60
```

로케이터 경로에서는 `maxGrowth = max(W,H)/3 + 12`로 성장을 제한해 맞닿은 텍스트·선으로 번지는 것을 막는다.

### 8.5 손상 등급 (`BarcodeGrader`) — ISO/IEC 15415 간이 근사

```
Rmin = 2% 퍼센타일,  Rmax = 98% 퍼센타일
SymbolContrast SC = (Rmax − Rmin) / 255
조기 F: Rmax − Rmin < 8

threshold = (Rmin + Rmax) / 2
margin    = (Rmax − Rmin) × 0.125
Modulation MOD = clamp( (mean(light) − mean(dark)) / (Rmax − Rmin), 0, 1 )
Defects    DEF = |{ v : |v − threshold| < margin }| / N
```

| 지표 | 4.0 (A) | 3.0 (B) | 2.0 (C) | 1.0 (D) | 0.0 (F) |
|---|---|---|---|---|---|
| SC | ≥ 0.70 | ≥ 0.55 | ≥ 0.40 | ≥ 0.20 | < 0.20 |
| MOD | ≥ 0.50 | ≥ 0.40 | ≥ 0.30 | ≥ 0.20 | < 0.20 |
| DEF | ≤ 0.15 | ≤ 0.20 | ≤ 0.25 | ≤ 0.30 | > 0.30 |

```
Score  = min(SC점수, MOD점수, DEF점수)        // 15415와 동일하게 최저값 채택
Letter = Score ≥ 3.5 ? 'A' : ≥ 2.5 ? 'B' : ≥ 1.5 ? 'C' : ≥ 0.5 ? 'D' : 'F'
표시   = "A (3.8)"
```

> 정식 15415 검증은 교정된 조명·광학계를 요구한다. 이 등급은 **손상 라벨 스크리닝용 참고값**이며
> 검교정 장비의 성적서를 대체하지 않는다.

### 8.6 DataMatrix 정책 — 표시 전용

사용자 요구에 따라 DataMatrix는 **바운딩 박스만 표시**하고 카운트·검증·GTIN 원천에서 제외한다.

```csharp
[JsonIgnore]
public bool IsDataMatrix =>
    Symbology.Contains("DataMatrix", StringComparison.OrdinalIgnoreCase);

/// <summary>검증 대상 바코드 — DataMatrix는 제외(박스만 표시), 나머지는 GS1 외형일 때만.</summary>
public static bool IsVerifiable(BarcodeHit hit) =>
    !hit.IsDataMatrix && (hit.IsGs1 || LooksGs1(hit.Text));
```

GS1 외형 판정:

```csharp
public static bool LooksGs1(string text) =>
    text.Contains('\x1d') || text.StartsWith("(01)")
    || text.StartsWith("]C1") || text.StartsWith("]d2") || text.StartsWith("]Q3")
    || (text.Length >= 16 && text.StartsWith("01") && text[2..16].All(char.IsDigit));
```

**예외** — 기준정보 DB의 기대 심볼로지 존재 확인(`MasterCheck`)에는 DataMatrix도 쓰인다.
"GS1 DataMatrix가 인쇄되어야 하는데 없다"는 검증은 값이 아니라 존재 여부이므로 정책과 충돌하지 않는다.

### 8.7 클릭 판독 (`DecodeAt`)

바코드 인식 모드에서 사용자가 클릭한 지점의 심볼 하나만 판독한다.

```
for window in [240, 480, 960]:
    점을 중심으로 window 크기 크롭 → 전체 파이프라인(Detect) 실행
    좌표를 원본 기준으로 복원 (h.Bbox.X + x0, ...)
    ① 점을 포함(±20px slack)하는 심볼이 있으면 → 가장 작은(안쪽) 것 반환
    ② 없으면 가장 가까운 심볼, 거리 ≤ window/2면 반환
    ③ 960 창에서도 못 찾으면 가장 가까운 것 반환
반환 없음 → null
```

거리 함수는 사각형까지의 유클리드 거리:

```
dx = max(box.X − x, 0, x − (box.X + box.W))
dy = max(box.Y − y, 0, y − (box.Y + box.H))
dist = √(dx² + dy²)
```

**겹치면 작은 심볼 우선** — 큰 박스 안에 작은 심볼이 들어 있는 경우 사용자가 클릭한 것은 보통 안쪽이다.

---

## 9. GS1 파싱 엔진

**파일**: `Gs1.cs`

### 9.1 AI 표

| AI | 고정장 | 최대장 | 이름 | 숫자 전용 |
|---|---|---|---|---|
| `00` | 18 | 18 | SSCC | ✓ |
| `01` | 14 | 14 | GTIN | ✓ |
| `02` | 14 | 14 | CONTENT GTIN | ✓ |
| `10` | 가변 | 20 | LOT | |
| `11` | 6 | 6 | PROD DATE | ✓ |
| `15` | 6 | 6 | BEST BEFORE | ✓ |
| `17` | 6 | 6 | EXP DATE | ✓ |
| `21` | 가변 | 20 | SERIAL | |
| `240` | 가변 | 30 | ADDITIONAL ID | |
| `30` | 가변 | 8 | VAR COUNT | |
| `310` | 7 | 7 | NET WEIGHT KG | (소수 지시자 `310x` 3자리 접두 매칭) |

### 9.2 전처리

```
1) 심볼로지 접두 제거:  ']' 로 시작하고 길이 ≥3 → 앞 3자 제거 (']C1', ']d2', ']Q3')
2) 괄호 표기 정규화:  "(01)0880…(10)ABC" → "010880…" + GS + "10ABC" + GS
   이때 괄호로 명시된 AI를 parenAis에 수집 (관대 모드의 미등록 AI 길이 판단에 사용)
```

### 9.3 순차 상태머신

```
pos = 0
while pos < len:
    if payload[pos] == GS: pos++; continue
    spec = MatchAi(payload, pos)                 // 4 → 3 → 2자리 순 최장 일치
    if spec == null: → 관대 모드 처리 (§9.4)
    pos += len(spec.Ai)
    if spec.FixedLen:
        value = payload[pos .. pos+FixedLen]
        if 숫자전용 AI ∧ 값에 비숫자 → Gs1ParseException     ← 'ABC'가 GTIN이 되는 것 방지
        pos += FixedLen
    else:
        end = payload.IndexOf(GS, pos) (없으면 len)
        value = payload[pos..end]
        if len(value) > MaxLen → Gs1ParseException
        pos = end
    Elements.Add(spec.Ai, spec.Name, value)
if Elements.Count == 0 → Gs1ParseException
```

`MatchAi`가 4→3→2 순서인 것이 중요하다. LOT 값 `10ABC1723`의 `17`이 AI로 오인되면 안 되는데,
가변장 AI는 GS를 만날 때까지 소비하므로 이 순서와 GS 규칙이 함께 보호한다(회귀 테스트 존재).

### 9.4 관대 모드 (`tolerant`, 기본 true)

사내 라벨에는 `(91)`·`(8012)` 같은 미등록 AI가 섞인다. 이것 하나 때문에 전체 파싱이 실패하면
**GTIN이 OCR 폴백으로 넘어가 거짓 합격**이 날 수 있다.

```
spec == null 일 때:
  digits = 앞 4자 중 선행 숫자
  if !tolerant ∨ len(digits) < 2 → 예외
  unknown = parenAis에 있는 4/3/2자리 후보 중 첫 번째, 없으면 digits[0..2]
  UnknownAis.Add(unknown)
  pos = 다음 GS 위치 (없으면 끝)
Partial = UnknownAis.Count > 0
```

등록된 AI(01/10/17)는 정상 대조되고, 화면에는 `(미등록 AI: 91)` 참고 행이 추가된다.

### 9.5 GTIN 직접 추출 (`TryExtractGtin`)

사용자 요구 규칙: **"맨 앞 응용식별자 01 다음 14자리를 GTIN으로 본다"**.
`(10)` 이후 구조가 깨져 있어도 GTIN만은 대조할 수 있게 한다.

```csharp
public static string? TryExtractGtin(string? payload)
{
    if (string.IsNullOrWhiteSpace(payload)) return null;
    var text = NormalizeParenthesized(StripSymbologyPrefix(payload.Trim()), []).TrimStart(GS);
    if (text.Length < 16 || !text.StartsWith("01", StringComparison.Ordinal)) return null;
    var gtin = text.Substring(2, 14);
    return gtin.All(char.IsDigit) ? gtin : null;
}
```

| 입력 | 결과 | 이유 |
|---|---|---|
| `]C10108806367067654` + `10ABC` | `08806367067654` | 접두 제거 후 01 시작 |
| `(01)08806367067654(10)ABC` | `08806367067654` | 괄호 정규화 후 01 시작 |
| `1025090776` + `0108806367067654` | `null` | `(01)`이 맨 앞이 아님 — 규칙 밖 |
| `010880636706` | `null` | 14자리 미만 |

### 9.6 FNC1 누락 복원

FNC1 없이 인쇄·디코드된 GS1-128은 가변장 `(10)` 값이 뒤따르는 AI까지 삼킨다.
예: LOT `25090776` + EXP `270509` → `(10)` 값이 `25090776` **`17270509`**로 읽힘.

```csharp
if (!matched && expected.Length > 0 && lot.Length > expected.Length
    && lot.StartsWith(expected, StringComparison.Ordinal)
    && TryParseTail(lot[expected.Length..]) is { } tail)
{
    checks.Add(new CrossCheckResult(source, "LOT", expected, expected, true));
    checks.Add(new CrossCheckResult(source, "FNC1 누락", lot[expected.Length..],
        "(참고) (10) 뒤 구분자 없이 이어진 AI를 분리해 대조", true));
    var repaired = new Gs1Message();
    foreach (var element in message.Elements.Where(e => e.Ai is not "01" and not "10"))
        repaired.Elements.Add(element);
    repaired.Elements.AddRange(tail.Elements);
    checks.AddRange(Check(repaired, record, source));   // 분리된 AI 재귀 대조
    return checks;
}
```

```csharp
private static Gs1Message? TryParseTail(string tail)
{
    if (tail.Length < 3 || !tail.All(char.IsDigit)) return null;   // 숫자 AI 열이 아니면 LOT 불일치
    try { return Gs1.Parse(tail, tolerant: false); }               // 엄격 모드 — 우연한 성공 방지
    catch (Gs1ParseException) { return null; }
}
```

**안전 조건 3가지**: ① 기대 LOT으로 **시작**해야 하고 ② 잔여가 **전부 숫자**여야 하며
③ **엄격 모드**로 온전히 파싱돼야 한다. 진짜 LOT 불일치(`25099999`)는 이 조건을 만족하지 못해
그대로 불일치로 남는다.

### 9.7 날짜 규칙

```
YYMMDD → 연도: YY ≤ 50 ? 2000+YY : 1900+YY
        DD == 00 → 해당 월 말일 (GS1 표준)
        MM ∉ [1,12] → null
```

---

## 10. 검사 엔진 — 판정 로직

**파일**: `Inspection.cs` — 프로그램의 심장부.

### 10.1 실행 순서

```csharp
public InspectionOutcome Inspect(LabelRecord record, string standardName,
    IReadOnlyList<OcrWord> words, IReadOnlyList<CrossCheckResult>? barcodeChecks = null,
    string extraSearch = "", (int W, int H)? pageSize = null,
    IReadOnlyList<BarcodeHit>? barcodes = null)
{
    // ① 단어 병합(문장 학습) → ② 교정 사전(오인식 치환)
    IReadOnlyList<OcrWord> mergedWords = Options.Merges?.Apply(words) ?? words.ToList();
    IReadOnlyList<OcrWord> effective = Options.Corrections is { } corrections
        ? corrections.Apply(mergedWords) : mergedWords;
    // ③ 규격 조회 → ④ 검사 값 생성 → ⑤ 필드별 카운트 → ⑥ 커스텀 필드 → ⑦ 검색어
}
```

순서가 중요하다. 병합이 교정보다 먼저여야 `"2024-" + "05-10"` → `"2024- 05-10"` → 교정으로
`"2024-05-10"`이 되는 연쇄가 성립한다.

### 10.2 검사 값 생성 (`BuildSearchTerms`)

```csharp
["LOT"]      = record.Lot.Trim()
["PRODUCTS"] = record.Products.Trim()
["PN"]       = record.Pn.Trim()
["REF"]      = record.Ref.Trim()
["MFG DATE"] = ReformatDate(record.MfgDate, standard.DateFormat)
["EXP DATE"] = ReformatDate(record.ExpDate, standard.DateFormat)
["GTIN"]     = record.Gtin.Length > 0 ? Schema.NormalizeGtin14(record.Gtin) : ""
// 중국 규격만
["CHINA"]    = Standards.ChinaCodeForRef(record.Ref) ?? ""
```

`ReformatDate`는 `yyyy-MM-dd` / `yyyy.MM.dd` / `yyyy/MM/dd` / `yyyyMMdd` 중 하나로 해석해
규격 포맷으로 재표기한다. 목록은 항상 `yyyy-MM-dd`로 저장되고 BSC 라벨은 `yyyy.MM.dd`로 인쇄되므로
이 변환이 두 표기를 잇는다.

### 10.3 필드 매칭 조건 (`TextCounts`) — 불리언 식

```
text = Charsets.Repair(fieldName, word.Text.Trim())      // 문자 제약 복원 (§14.3)

매칭 ⟺  ¬text.Contains("(01)")                          ① GTIN 문자열 오염 배제
      ∧ ContainsTerm(text, term)                         ② 부분 문자열 (혼동 허용 옵션)
      ∧ text.ToUpper() ∉ {LOT, PN, REF, MFG DATE, EXP DATE, PRODUCTS}   ③ 라벨의 필드명 자체 배제
      ∧ ¬text.ToUpper().EndsWith(':')                    ④ "LOT:" 같은 레이블 배제
      ∧ text.Length > 2                                  ⑤ 잡음 배제
      ∧ ¬∃w ∈ {Lasso, Stent, Delivery, Device, Use} : text.Contains(w)   ⑥ 제품명 오탐 배제
```

```csharp
private bool ContainsTerm(string text, string term)
{
    if (text.Contains(term, StringComparison.OrdinalIgnoreCase)) return true;
    return Options.AllowConfusables && Options.Corrections is { } corrections
        && corrections.ConfusableContains(text, term);
}
```

**GTIN은 별도 경로** — UDI 문자열에서 `(01)+14자리` 구간만 정규식으로 찾고,
바운딩 박스도 그 구간 비율로 잘라 반환한다(뒤따르는 `(10)` 등은 제외).

```csharp
private static readonly Regex GtinAi = new(@"\(01\)(\d{14})(?=\(|\s|$)", RegexOptions.Compiled);

private TextMatch? GtinMatch(string gtin14, OcrWord word)
{
    var text = Options.Charsets.Repair("GTIN", word.Text.Trim());
    if (Options.AllowConfusables && Options.Corrections is { } corrections)
        text = corrections.Canonicalize(text);
    var match = GtinAi.Match(text);
    if (!match.Success || match.Groups[1].Value != gtin14) return null;
    var (x, y, w, h) = word.Bbox;
    var length = Math.Max(1, text.Length);
    var subX = x + (int)((double)w * match.Index / length);        // 문자 위치 비례 분할
    var subW = Math.Max(1, (int)((double)w * match.Length / length));
    return new TextMatch("GTIN", word with { Bbox = (subX, y, subW, h) }, gtin14);
}
```

### 10.4 GTIN 추출 3단 우선순위 (사용자 확정 규칙)

```
① 바코드 판독  — 검증 대상 바코드(GS1-128 등, DataMatrix 제외)에서 Gs1.TryExtractGtin 성공
                 → 이것이 원천. 기대 GTIN과 같은 바코드마다 매치 1건(박스 = 바코드 위치)
                 → 값이 다르면 0건이며 OCR로 덮지 않는다
② 인쇄 텍스트  — 어느 바코드에서도 GTIN을 뽑지 못했을 때만 OCR '(01)+14자리'
③ 추출 실패    — 둘 다 없으면 ExtractionFailed = true (오류)
```

```csharp
private static List<TextMatch>? BarcodeGtinMatches(string gtin14,
                                                   IReadOnlyList<BarcodeHit>? barcodes)
{
    if (barcodes is not { Count: > 0 } || gtin14.Length == 0) return null;
    var matches = new List<TextMatch>();
    var extracted = false;
    foreach (var hit in barcodes)
    {
        if (!BarcodeDetector.IsVerifiable(hit)) continue;         // DataMatrix 제외
        if (Gs1.TryExtractGtin(hit.Text) is not { } gtin) continue;
        extracted = true;                                          // ← 원천 확정 플래그
        if (gtin == gtin14)
            matches.Add(new TextMatch("GTIN", new OcrWord($"(01){gtin}", hit.Bbox, 100), gtin14));
    }
    return extracted ? matches : null;   // null이면 호출 측이 OCR로 폴백
}
```

`extracted` 플래그가 핵심이다. **하나라도 GTIN을 뽑았으면 바코드가 원천**이므로 값이 달라도
빈 리스트(0건)를 반환한다. `null`은 "바코드에서 GTIN을 전혀 못 뽑았다"는 뜻이며 이때만 OCR로 간다.

**GTIN 기대 개수는 규격과 무관하게 1** — 여러 개 검출돼도 값은 같으므로 하나만 일치하면 합격.

```csharp
int? expected = standard.Counts.TryGetValue(fieldName, out var count) ? count : null;
var atLeastOne = fieldName == "GTIN";
if (atLeastOne) expected = 1;
```

### 10.5 영역 필터 (`ApplyZones`)

필드에 검출 허용 영역이 등록돼 있으면 영역 안의 검출만 인정한다.

```
중심 좌표 정규화:  cx = (bbox.X + bbox.W/2) / pageWidth
                  cy = (bbox.Y + bbox.H/2) / pageHeight
인정 조건 (마진 M = 0.04):
  ∃zone : (cx ≥ zone.X − M) ∧ (cx ≤ zone.X + zone.W + M)
        ∧ (cy ≥ zone.Y − M) ∧ (cy ≤ zone.Y + zone.H + M)
적용 영역: zone.Standard == "" (전체) ∨ zone.Standard == 현재 규격
```

4% 마진은 라벨 배치의 미세 이동을 흡수한다. 영역이 하나도 등록되지 않은 필드는 전 페이지를 대상으로 한다.

### 10.6 판정식

```
FieldResult.Gating   = (Expected > 0) ∧ (Term ≠ "")
FieldResult.Passed   = ¬Gating ∨ (AtLeastOne ? Found ≥ 1 : Found = Expected)

InspectionOutcome.Passed
  = (∀f ∈ Fields : f.Passed) ∧ (∀c ∈ BarcodeChecks : c.Matched)

FinalPassed = InspectorVerdict?.Passed ?? Passed          // 검사자 확인 합격 반영
```

**`Found = Expected` 등호**가 중요하다. `≥`가 아니라 정확히 같아야 한다 — 라벨 12장짜리 시트에
13개가 검출되면 다른 페이지의 내용이 섞였거나 오탐이라는 신호다.

`BarcodeChecks`에는 GS1 교차검증 외에 LOT 미매칭 강제 행, 기준정보 대조, 동일값 결과가 모두 들어가며
**하나라도 불일치면 페이지 전체가 '확인 필요'**가 된다.

### 10.7 판정 서명 (중복 저장 방지)

같은 페이지를 재방문·재검사해도 판정이 같으면 새 파일·이력이 쌓이지 않게 하는 비교 키.

```
parts = [규격명, LOT, REF, "P"|"C"]
      ⧺ Fields 정렬(Field 사전순)      → "필드:검출/기대표기"
      ⧺ BarcodeChecks 정렬(Field, BarcodeValue) → "필드=값:0|1"
      ⧺ (includeSearch ? [검색어] : [])
Signature = SHA1(join(parts, "|")) 앞 16자 소문자 hex
```

정렬을 넣어 **필드·바코드 순서와 무관**하게 같은 서명이 나오도록 했다.

| 용도 | `includeSearch` | 이유 |
|---|---|---|
| 자동 저장 스킵 판단 | `true` (기본) | 검색어가 바뀌면 저장 이미지의 박스가 달라짐 |
| 검사자 확인 합격 유효성 | `false` | 검색어는 판정 근거가 아님 — 검색만 바꿨다고 처리가 풀리면 안 됨 |

저장 판단에는 검사자 처리 여부까지 덧붙인다.

```csharp
private string PageSignature(int page, InspectionOutcome outcome) =>
    outcome.Signature() + (_overrides.TryGetValue(page, out var o)
        ? (o.Verdict.Passed ? "|INSP:P" : "|INSP:F") : "");
```

### 10.8 사유 요약 (`InspectionSummary.Describe`)

화면 배지 사유줄과 저장 이미지 요약 박스가 **같은 문구**를 쓴다.

```
합격:  바코드 있음 → "필드 n/n 일치 · 바코드 m건 일치"
       없음        → "필드 n/n 일치"
       (n = Gating 필드 수, m = '미등록 AI' 제외 바코드 검증 수)

확인 필요: 우선순위대로 수집
  ① 불합격 필드: ExtractionFailed ? "GTIN 추출 실패(바코드·OCR 없음)"
                                   : "필드 검출/기대표기"
  ② 바코드 불일치: "GS1 해석" → "GS1 해석 불가"
                   "LOT 매칭" → "LOT 미매칭"
                   그 외      → "필드 바코드 불일치"
  최대 maxItems(기본 3)개 + " 외 n건"
  항목이 하나도 없으면 "확인 필요"
```

### 10.9 검사 옵션 주입

```csharp
public sealed class InspectionOptions
{
    public HashSet<string> DisabledFields { get; init; } = [];   // LOT은 제외 불가
    public List<CustomFieldDef> CustomFields { get; init; } = [];
    public bool AllowConfusables { get; init; }
    public OcrCorrections? Corrections { get; init; }
    public FieldCharsets Charsets { get; init; } = new();
    public List<FieldZone> Zones { get; init; } = [];
    public WordMergeRules? Merges { get; init; }
}
```

`InspectorView.BuildEngine()`이 `settings.json`의 `fields.*` 섹션을 읽어 이 객체를 만들고
엔진을 새로 생성한다. 교정·병합·영역이 바뀔 때마다 `ReloadEngineAndReinspect()`가 호출된다.

---

## 11. LOT 자동 매칭

**파일**: `Inspection.cs` — `ExtractLotCandidates`, `MatchLot`, `SimilarityScore`

라벨에서 LOT을 읽어 목록의 어느 행을 검사할지 결정한다. **이 단계가 틀리면 다른 LOT과 대조되어
거짓 합격이 나므로** 실패 시 반드시 '확인 필요'를 강제한다.

### 11.1 후보 추출

```csharp
private static readonly Regex[] LotPatterns =
[
    new(@"^\d{8}$"),           // 25090776
    new(@"^\d{2}[A-Z]\d{3}$"), // 24A1234
    new(@"^[A-Z]{2}\d{4}$"),   // AB1234
    new(@"^[A-Z0-9]{5,10}$"),  // 범용
];

words.Select(w => w with { Text = Charsets.Repair("LOT", w.Text.Trim()) })
     .Where(w => w.Text.Length >= 4 && w.Confidence >= 30
              && LotPatterns.Any(p => p.IsMatch(w.Text)))
```

문자 제약 복원을 **먼저** 적용해 `2S090776` → `25090776`이 후보에 들어오게 한다.

### 11.2 3단계 매칭

```
1) exact         : 후보 텍스트가 목록 LOT과 완전 일치 → 즉시 반환
2) suffix_unique : 후보가 4자리 이상 전부 숫자이고, 끝 4자리가 같은 목록 LOT이 정확히 1개 → 즉시 반환
3) suffix_best   : 끝 4자리가 같은 LOT이 여러 개면 가중 유사도 최고값
매칭 실패 → null → lotUnmatched = true
```

### 11.3 가중 유사도 수식

```
stringSimilarity = Ratcliff/Obershelp(candidate, target)     ∈ [0,1]
matchingPrefix   = 앞에서부터 같은 문자 수
prefixScore      = matchingPrefix / min(len_c, len_t)
confidenceScore  = ocrConfidence / 100
lengthScore      = 1 − |len_c − len_t| / max(len_c, len_t)

Score = (0.40·stringSimilarity + 0.30·prefixScore
       + 0.20·confidenceScore  + 0.10·lengthScore) × 100
```

**Ratcliff/Obershelp** (Python `difflib.SequenceMatcher.ratio()`와 동일):

```
ratio(a,b) = 2 × M / (|a| + |b|)
M = 최장 공통 부분 문자열 길이 + 좌측 구간 재귀 M + 우측 구간 재귀 M
```

구현은 DP 없이 `Dictionary<int,int>` 롤링으로 최장 공통 부분 문자열을 찾고 좌우를 재귀 처리한다
(`MatchingCharacters`). 파이썬 레거시와 동일한 값을 내도록 이식했다.

### 11.4 미매칭 처리

```csharp
if (lotUnmatched)
    barcodeChecks.Add(new CrossCheckResult("LOT 매칭", "LOT", "(라벨에서 LOT 미검출)",
                                           record.Lot, Matched: false));
```

`BarcodeChecks`에 불일치 행을 강제로 넣어 `Outcome.Passed`가 반드시 false가 되게 한다.
UI는 `⚠ 미매칭` 경고, 슬롯 `?`, 상태바 경고를 표시하고 이전 선택 LOT으로 검사를 진행한다.

**수동 우선 규칙** — 사용자가 `Ctrl+L`로 LOT을 고르면 `_manualLotPages`에 페이지가 기록되어
이후 자동 매칭이 그 페이지를 덮지 않는다.

---

## 12. 규격 자동 선택

**파일**: `StandardDetector.cs`

라벨에 인쇄된 **양식 문서번호**로 검사 규격을 정한다. 목록의 STANDARD 열·국가별 매핑은 쓰지 않는다
(사용자 확정 규칙, 소스 가드 테스트로 회귀 방지).

### 12.1 서명 유도 — 표시명에서 자동 추출

```
display_name 이 "<문서번호>(Rev.<개정>)" 패턴  → Doc = 문서번호, Rev = 개정
   "PML-001(Rev.1)" → Doc="PML-001", Rev="1"
   "BSL-01(Rev.5)"  → Doc="BSL-01",  Rev="5"
display_name 이 ^[A-Z]\d{2,3}$ 패턴           → Rev = 표시명 (라벨엔 "Rev.A00"으로 인쇄)
   "A00" → Doc=null, Rev="A00"
그 외 라틴/숫자 포함                            → Doc = 표시명 전체
라틴/숫자 없음("중국")                          → 감지 대상 아님
+ doc_patterns 정규식 (있으면 최우선)
```

별도 설정 없이 `standards.json`의 표시명만으로 동작하는 것이 설계 의도다.

### 12.2 OCR 혼동 허용 정규식 (`Fuzzy`)

문자별로 혼동 그룹 문자 클래스를 만든다.

| 원문 | 정규식 조각 |
|---|---|
| `0` `O` `Q` `D` | `[0OQD]` |
| `1` `I` `L` | `[1IL\|]` |
| `2` `Z` / `5` `S` / `8` `B` / `6` `G` | `[2Z]` / `[5S]` / `[8B]` / `[6G]` |
| `-` `–` `—` `_` `~` | `[-–—_~ ]?` (생략 허용) |
| `.` `,` | `[.,]?` |
| 공백 | `\s*` |

```
DocRegex = (?<![A-Z0-9]) + Fuzzy(Doc) + (?![A-Z0-9])     // 단어 경계 (Rev.10이 Rev.1에 안 걸리게)
RevRegex = REV[.,:]?\s* + Fuzzy(Rev) + (?![A-Z0-9])
```

따라서 `BSL-O1`, `BSL 01`, `Rev.AOO`, `REV A0O`가 모두 올바르게 인식된다.

### 12.3 두 줄 매칭 — 인접성 판정

라벨은 보통 문서번호 아랫줄에 개정을 인쇄한다(`PML-001` / `Rev.1`).
문서번호 단어와 **인접한** Rev를 우선 짝짓는다.

```csharp
internal static bool Adjacent(OcrWord doc, OcrWord rev)
{
    if (ReferenceEquals(doc, rev) || doc.Bbox == rev.Bbox) return true;
    var lineH = max(doc.Bbox.H, rev.Bbox.H);
    var docCy = doc.Bbox.Y + doc.Bbox.H / 2.0;
    var revCy = rev.Bbox.Y + rev.Bbox.H / 2.0;

    // ① 같은 줄 오른쪽
    var sameLine = |revCy − docCy| ≤ lineH × 0.6
                 ∧ rev.Bbox.X ≥ doc.Bbox.X
                 ∧ rev.Bbox.X − (doc.Bbox.X + doc.Bbox.W) ≤ lineH × 3;

    // ② 바로 윗줄/아랫줄 + 가로 겹침
    var horizontallyNear = rev.Bbox.X + rev.Bbox.W > doc.Bbox.X − lineH × 2
                         ∧ rev.Bbox.X < doc.Bbox.X + doc.Bbox.W + lineH × 2;
    var stacked = |revCy − docCy| ≤ lineH × 2.2 ∧ horizontallyNear;

    return sameLine ∨ stacked;
}
```

인접 Rev가 없을 때만 페이지 어딘가의 Rev를 낮은 점수로 쓴다 —
**다른 문서의 Rev 표기가 같은 페이지에 있어도 문서번호 바로 아래의 Rev가 이긴다.**

### 12.4 단어 결합 (`Texts`)

OCR이 `Rev.`와 `A00`을 따로 읽는 경우를 위해, 같은 줄에서 인접한 2~3단어를 결합한 가상 텍스트도
검색 대상에 넣는다.

```
같은 줄:   |중심 y 차| ≤ max(H) × 0.6
인접:      next.X ≥ prev.X  ∧  next.X − (prev.X + prev.W) ≤ max(H) × 1.5
결합 박스: 합집합, 신뢰도: 최솟값
```

### 12.5 점수 체계와 우선순위

| 근거 | 점수 | 조건 |
|---|---:|---|
| `doc_patterns` 정규식 일치 | 1.00 | 규격별 명시 패턴 |
| 문서번호 + 인접 Rev | 0.95 | 두 줄 매칭 성공 |
| 문서번호 + 원거리 Rev | 0.85 | 인접 Rev 없음 |
| Rev만 (표시명 자체가 Rev 토큰, 예 `A00`) | 0.90 | |
| Rev만 (고유 Rev, 3자 이상) | 0.80 | `RevDistinctive` |
| 문서번호만 (후보 1개) | 0.70 | |
| 문서번호만 (후보 여럿) | 0.50 | **Ambiguous** — Standard = null |

모호(Ambiguous)일 때는 `Standard`가 null이라 UI가 **이전 규격을 유지**하고 후보를 주황으로 안내한다.
`PML-001`만 읽히면 Rev.1(MDR)/Rev.0(MDD) 중 무엇인지 알 수 없으므로 임의 선택하지 않는다.

### 12.6 전체 규격 선택 우선순위 (UI 계층)

```
① 이 페이지 수동 지정 (규격 버튼 클릭 → _manualStandardPages)
② 라벨 문서번호 감지 (StandardDetector, Standard ≠ null)
③ 양식 감지 규칙 (LabelFormDetector, 설정된 경우)
④ 커스텀 필드 → 규격 매핑
⑤ 이 페이지에 마지막으로 쓴 규격 (_pageStandard)
⑥ 현재 선택 규격 → 없으면 첫 규격
```

`basis` 문자열(`"수동"`, `"문서번호"`, `"양식"`, `"커스텀"`, `"모호"`, `"유지"`)이 UI의 '문서:' 줄 문구를 결정한다.

---

## 13. 보조 검증 계층

### 13.1 동일값 패턴 (`SameValueChecker`)

한 라벨 안 여러 위치에 같은 값이 인쇄되는 형식을 위한 검사. 3가지를 본다.

**① 개수** — `instances.Count < max(1, MinInstances)` → "개수 부족"

**② 값 상호 비교 (다수결 합의)**

```
consensus = argmax_g |{ i : Canonicalize(i.Value) = g }|      // 혼동 정규형으로 그룹핑
불일치 = { i : ¬ValuesEqual(i.Value, consensus) }
ValuesEqual(a,b) = (a = b) ∨ ConfusableEquals(a,b)
```

**③ 기준 배치 추적 + 드리프트 보정** — 스캔·출력 위치가 전체적으로 밀려도 객체 누락만 잡아내기 위해
**전역 이동량을 먼저 추정**한다.

```
정규화 중심:  cᵢ = ( (X + W/2)/pageW , (Y + H/2)/pageH )

드리프트 추정 (기준 위치 r 각각):
  n(r) = argmin_i dist(cᵢ, r)
  if dist(n(r), r) ≤ DriftSearchRadius (0.2):
      dx.add(n(r).x − r.x),  dy.add(n(r).y − r.y)
  drift = ( median(dx), median(dy) )               ← 중앙값 (이상치 내성)

누락 판정:
  shifted(r) = r + drift
  greedy 1:1 매칭 — 각 r에 대해 미사용 인스턴스 중 최근접
  dist ≤ MatchTolerance (0.08) 이면 매칭, 아니면 "객체 누락"
```

평균이 아니라 **중앙값**을 쓰는 이유: 한 객체가 누락되면 최근접 매칭이 엉뚱한 곳을 가리켜
평균이 크게 흔들리지만 중앙값은 견딘다.

**④ 기준 배치 자동 갱신** — `AutoLearn`(설정 `learning.same_value_layout`, 기본 꺼짐)이 켜져 있고
합격했을 때만 현재 배치를 `same_value_layouts.json`에 저장한다.

### 13.2 양식 감지 (`LabelFormDetector`)

문서번호 자동 인식으로 부족할 때 쓰는 **선택적** 보조 규칙.

```csharp
public sealed record LabelFormRule(string Name, string Standard,
    (double X, double Y, double W, double H) Region, string TextPattern, bool UseImage);
```

**텍스트 매칭** — 규칙 영역 안(중심 좌표 기준)의 단어 중 정규식 일치하는 것.

**이미지 템플릿 매칭** — 영역을 64×48 그레이 썸네일로 만들어 zero-mean unit-norm 벡터화 후 내적.

```
ExtractThumb: 영역을 64×48로 블록 평균 리샘플, 휘도 = (299R+587G+114B)/1000/255
정규화: NormalizeVector (GlyphLibrary와 동일)
score = Σ template[i] × current[i]        // = 상관계수
채택: score ≥ ImageMinScore (0.75)
```

**점수 합성**

```
텍스트 ∧ 이미지 → 0.5 + imageScore/2      (가산)
텍스트만        → 0.95
이미지만        → imageScore
최고 점수 규칙 채택 (min(1, score))
```

템플릿은 `form_templates.json`에 base64 float 블롭으로 저장된다.

### 13.3 라벨 유형 학습 (`LabelTypeProfiler`)

라벨의 **고정 문구**를 유형(PN+규격)별로 축적해 두고, 고정 문구가 빠진 라벨을 잡아낸다.

**토큰 필터 (잡음 배제)**

```
제외 조건:
  word.Confidence < MinTokenConfidence (75)          ← 저신뢰 = 잡음 가능성
  ¬IsWordLike(text)                                   ← 기호 조각 배제
  NumericLike: ^[\d.\-/():%]+$                        ← 숫자·날짜류 = 가변
  레코드 가변값(LOT·MFG·EXP·GTIN) 포함

IsWordLike(t) ⟺ |t| ≥ 3
              ∧ (영숫자 수 ≥ |t| × 0.6)
              ∧ (글자(letter) 수 ≥ 2)
```

`|`, `—`, `)(`, `..`, `A1`, `12A`는 모두 배제되고 `STERILE`, `LOT`, `MDR-2017/745`는 통과한다.

**이상 판정**

```
samples < MinSamples (기본 5) → 학습 중, 이상 판정 안 함

staticTokens = { t : count(t) / samples ≥ StaticThreshold (0.8) }
missing      = staticTokens \ current
fresh        = current \ 학습된 토큰 전체

IsAnomaly = |missing| > 0  ∨  |fresh| ≥ NewTokenAlarm (3)
```

**UI 강등 규칙** — `missing`이 없고 `fresh`만 있는 이상은 OCR 변동일 가능성이 높아
**팝업 대신 상태바·사유줄**로만 알리고, 자동 판정이 합격이면 표본으로 학습해 유형이 적응하게 한다.
고정 문구 누락일 때만 대화상자를 띄운다.

**표본 감쇠** — 표본이 200을 넘으면 `samples /= 2`, 모든 토큰 카운트도 절반으로 줄여
유형이 서서히 바뀌어도 적응한다.

### 13.4 기준정보 DB (`MasterCheck`)

PN별 REF·GTIN·기대 심볼로지를 사전 등록해 두고 목록 레코드·검출 결과와 대조한다.

```
REF     : record.Ref.Trim() == master.Ref.Trim()
GTIN    : NormalizeGtin14(record.Gtin) == NormalizeGtin14(master.Gtin)
심볼로지 : master.ExpectedSymbologies(쉼표 구분) 각각에 대해
          detected.Any(h => h.Symbology == symbology)   ← DataMatrix도 포함(존재 확인)
```

결과는 `Source = "기준DB"` 교차검증 행으로 판정에 반영된다.

---

## 14. 학습·교정 계층

LaVIS의 학습은 **사용자 등록형**(교정·병합·영역·양식)과 **자동 축적형**(4모듈)으로 나뉜다.
자동 축적형은 **기본 모두 꺼짐**이며 설정에서 개별로 켠다.

### 14.1 자동 학습 모듈 게이트

```csharp
public bool LearningEnabled(string module)
{
    if (Section("learning")[module] is JsonValue v && v.TryGetValue<bool>(out var enabled))
        return enabled;
    // 구버전 키 호환
    if (module == "label_type" && Section("type_learning")["enabled"] is JsonValue legacy
        && legacy.TryGetValue<bool>(out var legacyEnabled)) return legacyEnabled;
    return false;                                  // 기본 꺼짐
}
```

| 모듈 키 | 대상 | 호출 지점 | 저장 파일 |
|---|---|---|---|
| `glyph_patterns` | AWS 결과 → 글자 템플릿 | `PrefetchWorker` 분석 콜백 | `glyphs.json` |
| `label_type` | 고정 문구 프로필 | `CheckLabelType` | `label_profiles.json` |
| `same_value_layout` | 객체 기준 배치 | `SameValueChecker.Check` | `same_value_layouts.json` |
| `master_db` | 목록 로드 시 PN별 REF·GTIN | `LoadRecords` | `history.db` (label_master) |

마이그레이션은 기본값 보충보다 **먼저** 실행해 새 키 기본값 false가 구버전 설정을 덮지 않게 한다.

```csharp
if (Settings["type_learning"] is JsonObject typeLearning
    && typeLearning["enabled"] is JsonValue legacyEnabled
    && legacyEnabled.TryGetValue<bool>(out var wasEnabled))
{
    learning["label_type"] ??= JsonValue.Create(wasEnabled);
    typeLearning.Remove("enabled");
    changed = true;
}
```

### 14.2 교정 사전과 혼동 그룹 (`OcrCorrections`)

**내장 혼동 문자** (대표 문자로 수렴)

```
O o Q D → 0      I l | → 1      Z → 2      S → 5
B → 8            G → 6          T → 7
```

**교정 등록에서 혼동 규칙 자동 학습** — 같은 길이의 교정이면 문자쌍을 혼동 규칙으로 추가한다.

```csharp
private void LearnConfusablesFrom(string wrong, string right)
{
    if (wrong.Length != right.Length) return;
    for (var i = 0; i < wrong.Length; i++)
    {
        var w = wrong[i]; var r = right[i];
        if (w == r) continue;
        var canonical = Canonical(r);
        _canonical[w] = canonical;     // 양방향 모두 같은 대표 문자로 수렴
        _canonical[r] = canonical;
    }
}
```

**적용 3형태**

| 메서드 | 동작 | 사용처 |
|---|---|---|
| `Apply(words)` | 완전 일치 단어를 교정값으로 치환 | 검사 전처리 |
| `Canonicalize(text)` | 혼동 문자를 대표 문자로 치환한 정규형 | 유사 매칭·합의값 그룹핑 |
| `ConfusableContains(text, term)` | 정규형끼리 부분 문자열 비교 | `AllowConfusables` 매칭 |

### 14.3 필드 문자 제약 (`FieldCharsets`)

"이 필드에는 이 문자가 나올 수 없다"를 선언해 ① 혼동 문자를 자동 복원하고 ② 복원 불가 문자를 검출한다.

```
ConfusableGroups = ["Oo0QD", "Il|1", "Z2", "S5", "B8", "G6", "T7", "g9", "A4", "E3"]

RepairChar(rule, c):
  if ¬rule.IsViolation(c): return c
  candidate = null
  for group in ConfusableGroups where group.Contains(c):
      for other in group where other ≠ c ∧ ¬rule.IsViolation(other):
          if candidate ≠ null ∧ candidate ≠ other: return c     ← 후보 2개 = 모호 → 원문 유지
          candidate = other
  return candidate ?? c
```

**유일 복원 원칙** — 후보가 둘 이상이면 복원하지 않는다. 기본 설정에서 날짜·GTIN은 숫자만 허용하므로
`2024-05-1O` → `2024-05-10`이 유일하게 복원된다.

### 14.4 단어 병합 (`WordMergeRules`)

OCR이 나눠 읽는 문구를 한 단어로 합친다.

```
1) 읽기 순서 정렬:
   lineTolerance = 평균 높이 × 0.6
   세로 중심 오름차순으로 훑으며 (center − lineCenter > tolerance)이면 새 줄
   줄 안에서는 X 오름차순
2) 규칙 토큰 열이 같은 줄에서 연속 일치하면 병합
   텍스트 = 공백 결합, 박스 = 합집합, 신뢰도 = 최솟값
3) 중첩 병합을 위해 변화가 없을 때까지 최대 3회 반복
```

### 14.5 학습 데이터 이식

| 번들 | 확장자 | 포함 | 동작 |
|---|---|---|---|
| 학습 데이터 | `.lslearn` | `glyphs.json`, `ocr_corrections.json` | **병합**(Merge) — 기존 데이터 유지 |
| 프리셋 | `.lavispreset` | 설정 규칙 키 + 데이터 파일 8종 | **교체**(Replace) — 가져오기 전 자동 백업 |

프리셋이 담는 설정 키와 제외 규칙:

```csharp
public static readonly string[] SettingsKeys =
[
    "shelf_life_months", "ocr", "fields", "preprocess",
    "type_learning", "learning", "label_forms", "overlay", "pdf_render_zoom",
];
private static readonly Dictionary<string, string[]> ExcludedSubKeys = new()
{
    ["ocr"] = ["engine"],   // 엔진 선택은 PC 환경(AWS 자격증명 유무)에 따라 다름
};
public static readonly string[] DataFiles =
[
    AppConfig.StandardsFile, AppConfig.ColumnMapsFile,
    "glyphs.json", "ocr_corrections.json", "word_merges.json",
    "same_value_layouts.json", "form_templates.json", "label_profiles.json",
];
```

**제외되는 PC 고유값**: `save_directory`, `aws`, `ocr.engine`, `last_files`, `last_list_path`,
OCR 캐시, 이력 DB.

가져오기는 `backup-preset-<시각>` 폴더에 현재 상태를 보관한 뒤 `.tmp` → `Move` 원자적 교체로 적용한다.

---

## 15. UI/UX 구현

### 15.1 디자인 토큰 (`Theme.xaml`)

자매 제품 UDInspect와 공유하는 패밀리 디자인. 색·크기 리터럴을 XAML에 직접 쓰지 않고 토큰만 참조한다
(`ThemeTokenTests`가 인라인 색 사용을 금지).

| 토큰 | 값 | 용도 |
|---|---|---|
| `PrimaryBrush` | `#1A6FB5` | 주 동작 버튼 |
| `AccentBrush` | `#1A5276` | 제목·강조 텍스트 |
| `SuccessBrush` / `SuccessBgBrush` | `#1E8449` / `#D5F5E3` | 합격 |
| `WarnBrush` / `WarnBgBrush` | `#CA6F1E` / `#FDEBD0` | 확인 필요 |
| `FailFgBrush` / `FailBgBrush` | 붉은 계열 | 불일치·추출 실패 |
| `DangerButtonBrush` / `CautionButtonBrush` | `#D9534F` / `#F0AD4E` | 파괴적·주의 동작 |
| `OverlayBarcodeBrush` / `OverlayFormBrush` / `OverlayLowConfBrush` | `#008080` / `#8000A0` / `#FF8C00` | 오버레이 |
| `BigCountSize` / `ReadoutSize` / `SubCountSize` / `CaptionSize` | 56 / 24 / 15 / 11 | 크기 계층 |

**오버레이 색은 Core 상수와 동기화**되며 테스트가 hex 일치를 검증한다.

```csharp
public static readonly SKColor BarcodeBoxColor  = new(0, 128, 128);
public static readonly SKColor FormBoxColor     = new(128, 0, 160);
public static readonly SKColor LowConfidenceColor = new(255, 140, 0);
```

### 15.2 검사 탭 레이아웃

```
Grid (좌: * / 우: 430px)
├─ 좌측
│  ├─ GroupBox "라벨 PDF"
│  │   ├─ DockPanel 툴바 (우측 고정: 자동 저장·결과 저장 / 좌측 WrapPanel: 기능군)
│  │   └─ ItemsControl 페이지 슬롯 (UniformGrid Rows=1)
│  └─ GroupBox "검사 화면"
│      ├─ WrapPanel 범례 칩
│      ├─ Border 모드 배너 ×2 (영역 등록 / 바코드 인식)
│      └─ Grid [뷰어 60* | GridSplitter | 모아보기 40*]
│          ├─ ScrollViewer > Grid > [Image + Canvas(ZoneLayer, RubberBand)]
│          └─ Border > DockPanel > ScrollViewer > Viewbox > Grid(GalleryGrid)
└─ 우측 (Grid 5행: Auto/Auto/Auto/2*/3*)
   ├─ 집계 줄 (BigCount ×2 + 진행 + 미저장)
   ├─ GroupBox 검사 목록/규격 (목록·LOT·규격 라디오·문서·검색)
   ├─ Border 판정 배지 (헤드라인 + 사유줄)
   ├─ GroupBox 필드별 검출 (DataGrid 5열)
   └─ GroupBox 바코드 검증 (DataGrid 5열)
```

### 15.3 상호작용 규범 (HFE)

| 규범 | 구현 | 이유 |
|---|---|---|
| 확인 대화상자 기본 '아니오' | `Dialogs.Confirm(..., defaultNo: true)` | Enter 연타로 파괴적 동작이 실행되지 않게 |
| 버튼 라벨이 **동작**을 말함 | `ChoiceDialog` — "라벨을 확인하겠습니다 (학습 안 함)" / "정상 개정 — 새 유형으로 학습" | 예/아니오보다 오조작 감소 |
| 모든 모달 Esc 취소 | `IsCancel="True"` 버튼 필수 (소스 가드 테스트) | 탈출 경로 보장 |
| 정보성 팝업 강등 | 저장 완료 → 배지 플래시 + 상태바 | 팝업 피로 방지 |
| 경고·오류 5초 고정 | `MainWindow.ShowStatus`의 alert hold | 스캔마다 오는 정보 메시지에 덮이지 않게 |
| 색 없이도 읽힘 | 기호 `✓ ✗ ! ? –` 병기 | 색각 이상·흑백 인쇄 |
| 같은 원인 팝업 연쇄 차단 | 동일 메시지 60초 1회 + 프리페치 중단 | 페이지마다 팝업 방지 |

### 15.4 오버레이 렌더 (`Annotate`)

```csharp
using var annotated = Annotate.RenderOverlays(image, outcome.AllMatches,
                                              _standards.FieldColors, CurrentOverlayStyle());
Annotate.DrawBarcodeBoxes(annotated, analysis.Barcodes, CurrentOverlayStyle());
if (result.DocNumber is { Standard: not null } docBox)
    Annotate.DrawTaggedBox(annotated, docBox.Bbox, $"문서번호: {docBox.MatchedText}",
                           Annotate.FormBoxColor, CurrentOverlayStyle());
```

**필드 박스 여백**: `pad = max(3, h × 0.12)` — 글자에 달라붙지 않게 높이 비례.

**저신뢰 하이라이트 필터** (사용자 요구: 검출 대상이 아닌 객체에 박스를 그리지 않는다)

```csharp
var matchedBoxes = outcome.AllMatches.Select(m => m.Word.Bbox).ToList();
var lowWords = OcrQuality.LowConfidenceWords(analysis.Words, lowThreshold)
    .Where(w => matchedBoxes.Any(b => Intersects(b, w.Bbox))).ToList();

private static bool Intersects((int X,int Y,int W,int H) a, (int X,int Y,int W,int H) b) =>
    a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
```

**저장 이미지 요약 박스** (우상단 260px)

```
1행: [규격명] PASSED|CHECK
2행: 사유 요약 (화면 사유줄과 동일 문구, 34자 초과 시 말줄임)
2.5행: INSPECTOR PASS (auto CHECK)          ← 검사자 확인 합격 시에만
이후: 필드별 "LOT: 13/13 OK" / "REF: 12/13 NG"
색: 합격 RGB(0,130,0) / 불합격 RGB(200,0,0)
```

### 15.5 필드 모아보기 (`BuildGallery`)

같은 항목의 검출 객체 크롭을 표로 모아 육안 대조를 돕는다.

```
열 = 항목 (GalleryOrder: LOT, PN, REF, MFG DATE, EXP DATE, GTIN, CHINA → 그 외 사전순)
행 = max over fields of RowsFor(f)
     RowsFor(f) = max(f.Found, f.AtLeastOne ? 1 : f.Expected ?? 0)
칸 = CropRegion(image, match.Bbox)      // pad = max(3, bbox.H × 0.25)
```

| 상태 | 표시 |
|---|---|
| 머리글 | `LOT 13/13` — 배경 합격 녹 / 불합격 주황 / 추출 실패 빨강 |
| 정상 칸 | 크롭 이미지 (MaxHeight 34, MaxWidth 220, Uniform DownOnly) |
| 저신뢰 | 주황 테두리 2px (`Confidence < lowThreshold ∧ < 100`) |
| 기대 초과 | 노랑 테두리 2px (`¬AtLeastOne ∧ r ≥ expectedRows`) |
| 미검출 | 빨강 배경 + `미검출` / `추출 실패` |
| 칸 클릭 | `ScrollToBbox` — 뷰어를 그 위치로 스크롤 |

**자동 축척** — 표 전체를 `Viewbox Stretch="Uniform"`에 넣고 `HorizontalScrollBarVisibility="Disabled"`로
두어 패널 폭에 맞춰 통째로 확대·축소된다. 좌우 스크롤이 생기지 않는다.

**레이아웃 비율** — 뷰어 60* : 모아보기 40* 별표 비율이라 창 크기가 바뀌어도 3:2가 유지되고,
`GridSplitter` 드래그는 비율로 환산해 저장된다.

```csharp
var percent = (int)Math.Round(Math.Clamp(
    GalleryColumn.ActualWidth / total * 100, 10, 80));
_config.Section("overlay")["gallery_percent"] = JsonValue.Create(percent);
```

별도 열이므로 라벨을 확대해도 모아보기와 겹치지 않는다.

### 15.6 자동 검사 상태기계

```
Idle ──[▶ 자동 검사]──► Running ──[완료]──► Idle + 결과 목록(부적합만 보기)
                          │
                          ├─[■ 중지 / Esc]──► Canceled (판정 유지)
                          ├─[PDF 교체·설정 변경]──► Canceled (세대 불일치)
                          └─[3분 진척 없음]──► Aborted + 오류 안내
```

```csharp
for (var page = 0; page < total; page++)
{
    ct.ThrowIfCancellationRequested();
    while (!_analyses.ContainsKey(page))            // OCR 대기
    {
        if (!_pdf.IsOpen || _worker.Generation != generation) throw new OperationCanceledException();
        if (_failedPages.Contains(page)) break;      // OCR 실패 페이지는 건너뜀
        if (_analyses.Count != lastAnalyzed) { lastAnalyzed = _analyses.Count; lastProgressAt = Now; }
        else if (Now - lastProgressAt > TimeSpan.FromMinutes(3))
            throw new OperationCanceledException();  // 진척 없음 → 중단
        await Task.Delay(300, ct);
    }
    using var image = await Task.Run(() => RenderProcessedForAnalysis(page), ct);   // 소유 렌더
    var result = ComputePage(page, analysis, image, fallbackIndex);
    StorePageResult(page, result);
    if (page == _currentPage) ApplyPageUi(page, result, analysis, image, fit: false);
    AutoSaveIfNeeded(page, result.Outcome, image);
    if (page % 5 == 4) { UpdateDashboard(); UpdatePageSlots(); ResultsChanged?.Invoke(); }
    await Task.Yield();                              // UI 응답성 유지
}
```

**순수 계산 / UI 반영 분리** — `ComputePage`는 UI 컨트롤을 전혀 만지지 않는 순수 함수라
전 페이지 루프에서 안전하게 쓸 수 있고, `ApplyPageUi`는 현재 페이지에만 적용된다.

```csharp
private sealed record PageInspection(
    InspectionOutcome Outcome, LabelRecord Record, int RecordIndex, LotMatchResult? LotMatch,
    bool LotManual, bool LotUnmatched, string StandardKey, string StandardBasis,
    StandardDetection? DocNumber, FormDetection? Form, string? CustomFieldName);
```

### 15.7 검사자 확인 합격

```
조건: 검사됨 ∧ ¬EffectivePassed ∧ ¬LOT 미매칭
사유: InputDialog.Ask — 빈 문자열이면 처리하지 않음 (필수)
기록: InspectorVerdict(Passed: true, By: Environment.UserName, At: Now, Note: 사유)
저장: _overrides[page] = (verdict, outcome.Signature(includeSearch: false))
```

**자동 해제 규칙**

```csharp
if (_overrides.TryGetValue(page, out var existing))
{
    var basis = result.Outcome.Signature(includeSearch: false);
    if (result.Outcome.Passed)
        _overrides.Remove(page);            // 자동 판정이 합격 → 처리 불필요
    else if (existing.Basis != basis)
    {
        _overrides.Remove(page);            // 판정 근거가 바뀜 → 다시 확인 필요
        Status($"p{page+1}: 판정 근거가 바뀌어 검사자 확인 합격이 해제되었습니다 — 다시 확인하세요.",
               StatusLevel.Warn);
        AppLog.Warn($"p{page+1} 검사자 확인 합격 해제 (판정 서명 변경) — 이전 사유: {existing.Verdict.Note}");
    }
}
```

교정을 등록해 판정 내용이 달라지면 **이전 확인이 자동으로 무효화**되어 검사자가 다시 보게 된다.

### 15.8 페이지 슬롯 상태 매핑

| 조건 | 채움 | 테두리 | 기호 |
|---|---|---|---|
| 분석 없음 | 회색(`SlotIdleBrush`) | 얇음 | — |
| 분석됨·판정 전 | 흰색 | 얇음 | — |
| LOT 미매칭 | 주황 배경 | 주황 2px | `?` |
| 검사자 확인 합격 | 녹 배경 | **초록 2px** | `✓` |
| 합격 | 녹 배경 | 얇음 | `✓` |
| 확인 필요 | 주황 배경 | 얇음 | `!` |
| 저장됨 | + 하단 초록 3px 띠 | | |
| 현재 페이지 | | 파랑 3px + 굵게 | |

```
페이지 60개 초과 → 현재 페이지 주변 60개만 (first = clamp(current − 30, 0, count − 60))
슬롯 폭 < 18px → 숫자 생략, 기호만 (겹침 방지)
```

### 15.9 키보드 라우팅

`MainWindow`가 검사 탭 활성 시에만 `Inspector.HandleGlobalKey(e)`로 라우팅한다.

```csharp
// 텍스트 입력 중에는 개입하지 않는다 (검색창에서 N/F5는 문자 입력)
if (e.OriginalSource is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem) return;
```

| 키 | 동작 | 키 | 동작 |
|---|---|---|---|
| `Ctrl+O` | PDF 열기 | `N` | 다음 확인 대상 |
| `Ctrl+S` | 결과 저장 | `←/→/Home/End` | 페이지 이동 |
| `F5` | 재검사 | `W/A/S/D` | 화면 이동 (90px) |
| `Ctrl+L` | LOT 콤보 열기 | `Q/E`·휠 | 확대/축소 (8%/노치) |
| `Ctrl+F` | 검색창 포커스 | `Esc` | 모드 해제 / 자동 검사 중지 |
| `Ctrl+Shift+L` | OCR 로그 | | |

Ctrl 조합 처리 후 `return`해 단일 키(`S` = 아래로 이동)와 충돌하지 않게 한다.

### 15.10 상태바 3단계

```csharp
public enum StatusLevel { Info, Warn, Error }
```

`[HH:mm:ss] 메시지` 형식. 경고·오류는 **5초간 고정**되어 후속 정보 메시지에 덮이지 않는다.
오른쪽에 AWS/엔진 상태, 진행 막대, 버퍼 수, 버전(클릭 → About 오버레이), ⚙ 설정.

---

## 16. 데이터 임포트 / 익스포트

### 16.1 입력: 엑셀 3종 → 검사 목록

**컬럼 매핑** (`column_maps.json`, 0-기반 인덱스)

```json
{
  "schedule": { "sheet": "진행수량",
    "columns": {"lot": 5, "pn": 6, "products": 7, "ref": 9, "country": 13, "mfg": 23} },
  "product":  { "sheet": "품목번호리스트", "columns": {"pn": 0, "gtin": 11} },
  "bsc":      { "sheet": "현UPN별", "columns": {"ref": 12, "pn": 15, "gtin": 42} }
}
```

**생성 로직** (`ListGenerator.Generate`)

```
조회 테이블 사전 구축:
  productGtin : PN → GTIN         (품목번호 리스트, 첫 항목 우선)
  bscByPn     : PN → (REF, GTIN)  (BSC 리스트)

행마다:
  mfg = CoerceDate(row[mfg])                    // DateTime | 엑셀 시리얼(20000~80000) | 파싱 가능 문자열
  선택 날짜에 없으면 skip
  exp = mfg.AddMonths(shelf_life_months).AddDays(-1)     // 월말 자동 보정
  (refBase, warning) = CleanRef(row[ref])

  일본(country == "일본"):
     REF  = bscByPn[pn].Ref  (없으면 스케줄 값 + 경고)
     GTIN = bscByPn[pn].Gtin (없으면 productGtin[pn] + 경고)
  그 외:
     REF  = refBase
     GTIN = productGtin[pn]  (없으면 빈칸 + 경고)

  GTIN = Schema.NormalizeGtin14(raw)            // 실패 시 원본 유지 + 경고
  날짜 표기 = yyyy-MM-dd (고정)
  STANDARD = 없음                                ← 규격은 라벨 문서번호로 (§12)

  행 단위 예외는 RowIssue("error")로 기록하고 계속 진행 (무단 소멸 금지)
결과 정렬: 제조일 오름차순 그룹 → 그룹 내 원본 순서
```

**REF 정제** (`CleanRef`) — 명백한 날짜만 비우고 경고를 남긴다(레거시의 과잉 휴리스틱 제거).

```
DateTime            → "" + "REF 셀이 날짜 값(...)이라 비웠습니다."
double ∧ 정수 ∧ 40000~60000 → "" + "엑셀 날짜 시리얼로 보여 비웠습니다."
double              → 정수면 정수 표기, 아니면 그대로
문자열이 날짜 정규식 일치 → "" + 경고
그 외                → 원본
```

### 16.2 GTIN 정규화 (`Schema.NormalizeGtin14`) — 단일 규칙

```
1) "nan"/"none"/빈 값 → ""
2) 부동소수 아티팩트 ^\d+\.0+$      → 소수점 앞만
3) 지수 표기 ^[\d.]+[eE]\+?\d+$     → (long)double 변환
4) GS1 표기 ^\(01\)(\d{1,14})$ 또는 ^01(\d{14})$ → 캡처 그룹
5) 숫자만 남김 → 14자리 zero-pad
6) 14자리 초과 → SchemaException
```

엑셀이 GTIN을 숫자로 읽어 `8.80637E+12`로 바꾸거나 `.0`을 붙이는 문제를 모두 흡수한다.

### 16.3 검사 목록 파일 계약

```
시트명: "Label Inspection List" (없으면 첫 시트)
헤더:   LOT, PRODUCTS, PN, REF, MFG DATE, EXP DATE, GTIN   (필수, 순서 무관)
선택:   STANDARD (읽어도 무시 — 구버전 호환)
```

저장 시 GTIN 셀은 `NumberFormat = "@"`(텍스트)로 지정해 선행 0을 보존하고, 첫 행을 고정한다.

### 16.4 결과 이미지

```
파일명: {counter:D3}_{LOT}_{REF}_{yyyyMMdd}_{Passed|Check}.jpg
  counter: history.db의 counters 테이블 (재시작 후에도 연속)
  LOT/REF: PathRules.SanitizeFileStem — 영숫자·'-'·'_'만, 40자 절단, 빈 값은 NOLOT/NOREF
  판정: EffectivePassed (검사자 확인 합격 반영)
중복 회피: PathRules.UniquePath → "이름_(2).jpg", "_(3)"…   (기존 결과 덮어쓰기 금지)
축소: save_scale (기본 0.5), 품질: jpeg_quality (기본 90)
```

### 16.5 CSV 내보내기 — 24열 계약

```
PAGE, LOT, PN, REF, GTIN, MFG_DATE, EXP_DATE, STANDARD,
AUTO(자동판정), INSPECTOR(검사자처리), FINAL(최종),
FIELD_LOT, FIELD_PN, FIELD_REF, FIELD_MFG, FIELD_EXP, FIELD_GTIN, BARCODES,
INSPECTOR_NOTE, INSPECTOR_BY, IMAGE_PATH, PDF_FILE, EXPORTED_AT, APP_VERSION
```

| 열 | 값 | 비고 |
|---|---|---|
| `PAGE` | 1-based | 미검사 페이지도 한 행 |
| `AUTO` | `MATCH` / `CHECK` / `UNINSPECTED` | 자동 판정 (보존) |
| `INSPECTOR` | `` / `PASS` / `FAIL` | 검사자 처리 |
| `FINAL` | 검사자 처리가 있으면 그것, 없으면 AUTO | 최종 판정 |
| `FIELD_*` | `13/13`, `1/≥1`, `-` | `ExpectedDisplay` 사용 |
| `BARCODES` | `GTIN:OK;LOT:NG` | 세미콜론 구분 |
| `APP_VERSION` | CI 정보 버전 | 도구 식별 |

**엑셀 텍스트 보호** — LOT/PN/REF/GTIN/IMAGE_PATH는 `="값"` 수식 형태로 감싼다.

```csharp
public static string TextField(string s) =>
    string.IsNullOrEmpty(s) ? "" : "\"=\"\"" + s.Replace("\"", "\"\"") + "\"\"\"";
// 결과: "=""00123"""  → 엑셀에서 문자열 00123 (선행 0 보존, 지수 표기 없음)
```

외부 도구(pandas 등)에서 읽을 때는 `CsvUtil.Unwrap`으로 벗기면 된다. 인코딩은 UTF-8 BOM, 줄바꿈 CRLF.

**열 수 불변 보장** — `Row()`가 마지막에 스스로 검증한다.

```csharp
if (cells.Count != ColumnCount)
    throw new InvalidOperationException($"CSV 열 수 불일치: {cells.Count} != {ColumnCount}");
```

**내보내기 전 확인 게이트**

```csharp
public bool NeedsConfirmation => Check + Uninspected + Unsaved > 0;
public string ConfirmMessage() =>
    $"확인 필요 {Check}건 · 미검사 {Uninspected}건 (PDF {Total}페이지 중) · 결과 미저장 {Unsaved}건이 포함됩니다.\n" +
    "프로그램 표시는 참고값이며 최종 판정은 검사자가 합니다.\n그래도 내보낼까요?";
```

### 16.6 LOT 리포트 (`Report.ExportLotReport`)

xlsx 2시트.

```
시트 "요약"  : LOT, 검사 건수, 합격, 확인 필요, 합격률(%)
시트 "검사 상세": 일시, LOT, REF, PN, 규격, 소스, 페이지, 판정, 이미지 경로, 필드별 검출/기준
```

### 16.7 파일 쓰기 보호막 (`ExportGuard`)

모든 내보내기가 같은 예외 → 한국어 안내 매핑을 공유한다.

| HRESULT / 예외 | 안내 |
|---|---|
| `0x80070020` 공유 위반 / `0x80070021` 잠금 | "파일이 다른 프로그램(엑셀 등)에서 열려 있는지 확인 후 다시 시도하세요. 검사 결과는 그대로 유지됩니다." |
| `0x80070070` 디스크 가득 | "디스크 공간이 부족합니다." |
| `0x80070035` / `0x80070043` 네트워크 경로 | "네트워크 경로를 찾을 수 없습니다: {폴더}" |
| `UnauthorizedAccessException` | "폴더에 쓰기 권한이 없습니다: {폴더}" |
| `DirectoryNotFoundException` | "드라이브/네트워크 연결과 경로를 확인하세요" |
| 그 외 | `app.log` 기록 + "생성 실패: {메시지} (자세한 내용은 app.log)" |

**실패해도 검사 결과는 메모리에 유지**되므로 경로를 고쳐 다시 시도할 수 있다.

### 16.8 데이터 폴더 전체

`%APPDATA%\LaVIS` (구 `LabelSuite` 폴더는 첫 실행 시 자동 이전)

| 파일 | 내용 | 프리셋 포함 |
|---|---|---|
| `settings.json` | 사용자 설정 | 규칙 키만 |
| `standards.json` | 규격 정의 | ✓ |
| `column_maps.json` | 엑셀 컬럼 매핑 | ✓ |
| `glyphs.json` | 글자 패턴 템플릿 | ✓ (`.lslearn`도) |
| `ocr_corrections.json` | 교정 사전 | ✓ (`.lslearn`도) |
| `word_merges.json` | 병합 규칙 | ✓ |
| `same_value_layouts.json` | 동일값 기준 배치 | ✓ |
| `form_templates.json` | 양식 이미지 템플릿 | ✓ |
| `label_profiles.json` | 라벨 유형 프로필 | ✓ |
| `history.db` (+ `-wal`, `-shm`) | 검사 이력·카운터·기준정보 | ✗ |
| `ocr_cache/*.json` | 페이지 분석 캐시 | ✗ |
| `app.log` | 경고·오류·감사 줄 (2MB 회전) | ✗ |
| `crash.log` | 비UI 스레드 예외 | ✗ |

---

## 17. 설정 시스템

**파일**: `AppConfig.cs`, `SettingRanges.cs`, `Services/InputRules.cs`

### 17.1 로드 파이프라인

```
AppConfig(directory)
 ├─ MigrateLegacyDataDir()    %APPDATA%\LabelSuite → %APPDATA%\LaVIS 이전
 └─ Reload()
     ├─ 3개 파일 로드 (settings / standards / column_maps)
     │    파싱 실패 → .corrupt-<시각>으로 보관 후 번들 기본값 복구 + RecoveredFiles 기록
     ├─ Migrate()
     │    ├─ schema_version 단계 이전 (v1 → v2)
     │    ├─ 구버전 키 이전 (type_learning.enabled → learning.label_type)  ← 기본값 보충보다 먼저
     │    └─ 번들 기본값의 새 키 보충 (재귀 병합)
     └─ Normalize()
          └─ 범위 밖·형식 오류 값을 보정하고 Corrections 목록에 기록
```

시작 시 `Corrections`가 비어 있지 않으면 상태바·대화상자로 "설정 n건이 자동 보정되었습니다"를 안내한다.
`SettingsFromNewerVersion`(파일의 `schema_version`이 프로그램보다 큼)이면 경고한다.

### 17.2 범위표 — 단일 출처 원칙

```csharp
public sealed record RangeDef(string Path, double Min, double Max, double Default,
                              bool Integer, string Unit, string Label)
{
    public double Clamp(double value) {
        var v = Math.Clamp(value, Min, Max);
        return Integer ? Math.Round(v) : v;
    }
    public int ParseInt(string? text) =>
        int.TryParse(..., out var v) ? (int)Clamp(v) : IntDefault;
}
```

`SettingRanges.All` 하나를 **세 곳이 공유**한다.

1. `AppConfig.Normalize` — 로드 시 자동 보정
2. `SettingsWindow` + `InputRules` — 입력란 실시간 검증(붉은 배경·범위 툴팁·저장 차단·탭 전환)
3. `AppSourceGuardTests.SettingsXamlTagsMatchSettingRanges` — XAML `Tag`와 범위표의 **양방향 일치** 검증

```csharp
// 범위표에 없는 Tag가 있으면 실패 (InputRules가 배선하지 못함)
var unknown = tags.Where(t => !known.Contains(t)).ToList();
// 범위표에 있는데 입력란이 없으면 실패 (UI 비노출 예외 목록 제외)
string[] notInUi = ["ocr_cache_max_entries", "page_image_cache_pages", "overlay.gallery_percent"];
var missing = SettingRanges.All.Select(d => d.Path)
    .Where(p => !p.Contains("[]") && !notInUi.Contains(p) && !tags.Contains(p)).ToList();
```

이 테스트 덕분에 설정을 추가하면서 UI 배선이나 범위 등록을 빠뜨릴 수 없다.

### 17.3 원자적 저장

```
1) settings.json.tmp 에 기록
2) File.Move(tmp, settings.json, overwrite: true)
```

쓰기 도중 정전·강제 종료가 나도 기존 파일이 온전히 남는다.

### 17.4 정규화 규칙 (`Normalize`)

| 대상 | 규칙 | 보정 예 |
|---|---|---|
| 숫자 설정 | `RangeDef.Clamp` | `pdf_render_zoom: 99 → 8` |
| `ocr.engine` | `aws`/`pattern`/`onnx` 외 → `aws` | |
| `prefetch_policy` | `"all"` 또는 숫자, 그 외 → `"all"` | |
| `save_directory` | `PathRules.IsValidDirectory` 실패 → `""` | 상대 경로·금지 문자 |
| 배열 필드 | 배열 아니면 `[]`로 교체 | |
| `fields.zones[].region` | 0~100 숫자 4개가 아니면 항목 제거 | |
| `fields.custom[]` | `name` 비었거나 중복이면 제거(첫 항목 유지) | |
| `fields.charsets[]` | `field` 비었으면 제거 | |
| `fields.disabled[]` | 비활성 불가 필드(LOT) 제거 | |
| `overlay.colors[].rgba` | 0~255 clamp | |

모든 보정은 `Corrections.Add($"{경로}: {이전} → {이후}")`로 기록되어 사용자에게 보고된다.

---

## 18. 견고성·오류 처리

### 18.1 거짓 합격 방지 장치 (최우선 원칙)

| 상황 | 방어 |
|---|---|
| 라벨에서 LOT 미검출 | 교차검증에 불일치 행 강제 삽입 → `Passed = false`, 슬롯 `?` |
| GS1 구조 해석 실패 | GTIN조차 못 뽑으면 `GS1 해석` 불일치 행 → 확인 필요 |
| 바코드 GTIN ≠ 목록 | 0건 유지, **OCR로 덮지 않음** (`extracted` 플래그) |
| GTIN 바코드·OCR 모두 실패 | `ExtractionFailed` → 붉은 행 + 상태바 오류 |
| OCR 엔진 실패 | 예외 전파 (빈 결과 위장 금지), 페이지는 미검사로 남음 |
| 규격 모호 | 이전 규격 유지 + 주황 안내 (임의 선택 금지) |
| 판정 근거 변경 | 검사자 확인 합격 자동 해제 + 경고 |

### 18.2 데이터 손상 복구

| 대상 | 복구 |
|---|---|
| `settings.json` 등 JSON | `.corrupt-<시각>`으로 보관 → 번들 기본값 복구 → 시작 후 안내 |
| `history.db` 손상·잠김 | 보관 후 새 DB 생성 → 안내 (검사는 계속 가능) |
| `glyphs.json`·학습 파일 | `catch (IOException or JsonException)` → 빈 상태로 시작 |
| OCR 캐시 파일 | 읽기 실패 시 null 반환 → 재분석 |

### 18.3 재과금 방지 (AWS Textract)

```
① OcrCache — 문서·페이지·배율·전처리·엔진 서명 키로 재사용
② PrefetchWorker 중복 제출 차단 — HashSet<(Generation, Page)>
③ 세대 태깅 — 문서 교체 시 대기 잡 폐기
④ 실패 연쇄 차단 — 같은 원인 60초 1회 알림 + 남은 프리페치 중단
⑤ 재OCR 명시 확인 — [인식 강화 후 재OCR]은 주황 버튼 + 과금 경고
```

```csharp
var sameAsLast = message == _lastFailureMessage
                 && DateTime.Now - _lastFailureShownAt < TimeSpan.FromSeconds(60);
if (sameAsLast) return;
_worker.NewGeneration();   // 대기 중인 프리페치 잡 폐기 (재과금·연쇄 실패 방지)
```

### 18.4 메모리·리소스

| 위험 | 방어 |
|---|---|
| 고배율 렌더 메모리 폭주 | 비트맵 60MB 초과 시 캐시 6 → 2 |
| 정렬 비트맵 누수 | `Owned` 플래그로 소유 추적, LRU 축출 시에만 Dispose |
| 분석 중 비트맵 해제 | 워커는 항상 `RenderPageOwned` 사용 |
| 페이지 수백 장 슬롯 | 현재 주변 60개만 렌더 |
| 이중 실행 | `Mutex(@"Local\LaVIS.SingleInstance")` |

### 18.5 예외 로깅

```csharp
// App.xaml.cs — 비UI 스레드 예외까지 포착
AppDomain.CurrentDomain.UnhandledException += ...      → crash.log
TaskScheduler.UnobservedTaskException += ...           → crash.log
DispatcherUnhandledException += ...                    → 대화상자 + crash.log
```

`app.log`는 2MB 회전이며 설정 폴백·저장 실패·검사자 처리를 남긴다.

### 18.6 저장 경로 사전 확인

```csharp
private static async Task<bool> ProbeDirectoryAsync(string dir)
{
    var probe = Task.Run(() => Directory.Exists(dir)
        || (Path.GetDirectoryName(Path.GetFullPath(dir)) is { Length: > 0 } parent
            && Directory.Exists(parent)));
    var finished = await Task.WhenAny(probe, Task.Delay(3000));   // 3초 타임아웃
    return finished == probe && probe.Result;
}
```

네트워크 드라이브가 끊기면 UI가 멈추지 않고 3초 후 자동 저장을 끄고 경고한다.

---

## 19. 빌드·배포·CI

### 19.1 워크플로 (`.github/workflows/build-csharp.yml`)

트리거: `workflow_dispatch` 또는 태그 `cs-v*`. 러너: `windows-latest`.

```
1. Checkout
2. Set up .NET 8.0.x
3. Source guard         — 상태 헬퍼 자기 재귀 정규식 검사 (pwsh)
4. Run core tests       — trx 로거, 실패 시 빌드 중단
5. Upload test results  — core-test-results 아티팩트
6. Resolve version      — 태그면 태그명, 아니면 sha7
7. Publish              — win-x64 self-contained, -p:InformationalVersion=<라벨>
8. Add distribution README — 13개 데이터 파일 설명
9. Smoke check          — LaVIS.exe·models 폴더 존재 확인, exe SHA-256 계산
10. Package zip         — LaVIS-<sha7>.zip
11. Upload artifact
12. Create Release      — 태그일 때만
```

### 19.2 빌드 식별 (밸리데이션 요구)

```
InformationalVersion  → App.InformationalVersion
   ├─ 상태바 버전 표시 → 클릭 시 About 오버레이
   ├─ HistoryDb.AppVersion → inspections.app_version 열
   └─ CSV APP_VERSION 열
exe SHA-256 → README.txt + 릴리스 본문
```

검증 문서의 "도구 식별" 란에 그대로 옮겨 적을 수 있다.

### 19.3 Linux에서 가능한 검증

XAML 컴파일은 Windows 전용이지만, 개발 중 다음까지는 Linux에서 확인된다.

```bash
# ① Core 빌드·테스트
dotnet test csharp/tests/LabelSuite.Core.Tests

# ② App 코드비하인드 컴파일 (WindowsDesktop 참조 어셈블리 8.0.31 사용)
#    XAML partial class 스텁을 생성해 net8.0으로 컴파일 → C# 오류를 Windows CI 전에 포착

# ③ XAML 정적 점검
#    - XML 파싱
#    - Control 전용 속성(TabIndex·IsTabStop·Foreground·Font*)이 Panel/Border/도형에 있는지
#    - 리소스 키 존재, enum 값 유효성, Style Setter 대상 타입 적합성
#    - 중복 x:Name, 코드비하인드에 없는 이벤트 핸들러
```

③의 Control 전용 속성 검사는 `XamlControlOnlyAttributeTests`로 자동화되어 있다
(과거 `WrapPanel TabIndex` MC3072 빌드 실패의 재발 방지).

---

## 20. 테스트 전략

xunit 310건 / 28개 파일 / 265개 테스트 메서드(`[Theory]`는 `InlineData`로 확장).

| 파일 | 메서드 | 검증 대상 |
|---|---:|---|
| `EnhancementTests` | 18 | 영역·문자 제약·품질·프리페치 등 복합 기능 |
| `Gs1TolerantAndSummaryTests` | 18 | 관대 파싱·바코드 검증 규칙·FNC1 복원·판정 서명·사유 요약 |
| `NormalizeTests` | 17 | 설정 정규화·범위표 정합·기본값 일치 |
| `ZoneAndAlignTests` | 17 | 영역 필터·이형지 크롭·기울기 보정 |
| `ListGeneratorTests` | 16 | 목록 생성(국가별·날짜·GTIN·경고) |
| `Gs1Tests` | 15 | AI 파서 상태머신·날짜·교차검증 |
| `InspectionTests` | 15 | 필드 카운트·판정식·GTIN ≥1 규칙 |
| `HistoryAndCacheTests` | 12 | 이력 DB·카운터·OCR 캐시 LRU |
| `SameValueAndProfilerTests` | 12 | 동일값 드리프트·유형 학습 |
| `MergeAndBarcodeGtinTests` | 11 | 병합 규칙·GTIN 원천 순서 |
| `StandardDetectorTests` | 11 | 문서번호 판별(두 줄 Rev·혼동·모호) |
| `AppSourceGuardTests` | 10 | **소스 규약** (아래) |
| `FieldCharsetTests` | 10 | 문자 제약 복원·위반 검출 |
| `FormDetectAndQualityTests` | 10 | 양식 감지·OCR 품질 |
| `SchemaTests` | 9 | 목록 계약·GTIN 정규화 |
| `CsvUtilTests` | 8 | CSV 파싱·텍스트 보호·24열 계약 |
| `GtinAtLeastOneAndProfilerNoiseTests` | 8 | GTIN ≥1·잡음 토큰 배제·학습 설정 |
| `BarcodeDetectorEndToEndTests` | 7 | **합성 렌더 종단 테스트** (아래) |
| `GlyphOcrTests` | 6 | 글리프 학습·인식 왕복 |
| `SessionStatsTests` | 6 | 세션 집계·다음 확인 대상 |
| `CredentialErrorTests` | 5 | AWS 오류 한글 변환 |
| `ThemeTokenTests` | 5 | 테마 토큰 ↔ Core 색 상수 일치 |
| `ConfigRecoveryTests` | 4 | 손상 파일 복구 |
| `PresetBundleTests` | 4 | 프리셋 내보내기·가져오기·백업 |
| `BarcodeBoxRefinerTests` | 3 | 박스 정밀화 |
| `InspectorVerdictTests` | 3 | 검사자 처리 기록·DB 마이그레이션 |
| `LearningBundleTests` | 3 | 학습 번들 병합 |
| `OnnxOcrTests` | 2 | ONNX 엔진 |

### 20.1 합성 렌더 종단 테스트

ZXing 인코더로 **실제 DataMatrix/Code128을 생성**해 큰 페이지에 배치하고 검출·박스 정합까지 검증한다.

```csharp
using var page = new SKBitmap(1200, 900);
canvas.Clear(SKColors.White);
using var symbol = Encode(BarcodeFormat.DATA_MATRIX, "(01)08806173612345(10)25090776", 120, 120);
canvas.DrawBitmap(symbol, 100, 100);
...
var hits = BarcodeDetector.Detect(page);
AssertBoxCovers(hit.Bbox, 100, 100, 120, 120);   // 중심 포함 + 크기 0.55~1.4배
```

재현 시나리오: 크기·위치가 다른 심볼 3개 / 하단 구석 44px 소형 심볼 + 텍스트 가득 / 프레임 선·텍스트에
맞닿은 심볼 / 로케이터 경로 단독 / 클릭 판독.

### 20.2 소스 규약 가드

컴파일은 되지만 **실행 즉시 죽거나 규칙을 위반하는 실수**를 소스 텍스트 수준에서 잡는다.

| 테스트 | 금지 사항 |
|---|---|
| `NoSelfRecursiveStatusHelper` | `void Status(...) => Status(` 자기 재귀 (StackOverflow) |
| `StatusHelperForwardsToEvent` | 상태 헬퍼가 `StatusMessage?.Invoke(message, level)`로 전달할 것 |
| `MessageBoxOnlyThroughDialogs` | `MessageBox.Show` 직접 호출 (공용 대화상자 규범 우회) |
| `YesNoOnlyInDialogs` | `MessageBoxButton.YesNo` 직접 사용 |
| `EveryWindowXamlHasCancelButton` | 모든 Window에 `IsCancel="True"` (Esc 탈출 경로) |
| `SettingsXamlTagsMatchSettingRanges` | 설정 Tag ↔ 범위표 양방향 일치 |
| `XamlControlOnlyAttributeTests` | Panel/Border/도형에 Control 전용 속성 (MC3072) |
| `RuleRegressionGuardTests` | 목록 STANDARD 열로 규격 선택 금지, DataMatrix 검증 표 제외 유지, 국가별 매핑 부활 금지 |

### 20.3 실기 체크리스트

`docs/manual_checklist.md` V-00 ~ V-13. 빌드마다 Windows 실기에서 점검한다.
V-04(바코드·GTIN), V-10(문서번호 규격 선택), V-11(자동 검사·결과 목록·검사자 처리),
V-12(GTIN ≥1·모아보기·유형 알람), V-13(3:2 레이아웃·자동 학습·바코드 인식)이 최근 기능에 해당한다.

---

## 21. 성능 특성

### 21.1 단계별 소요 (A4 라벨 시트, 렌더 배율 4.0 ≈ 2384×3370px 기준 추정)

| 단계 | 시간 | 지배 요인 |
|---|---|---|
| PDF 렌더 | 0.3~0.8초/페이지 | PDFium, 배율 제곱 비례 |
| 이형지 크롭 + 기울기 보정 | 0.2~0.5초 | 잉크 화소 수, 각도 탐색 25+9회 |
| AWS Textract | 1~3초 | 네트워크 왕복 + 전송 크기(maxDimension) |
| 패턴 학습 OCR | 0.3~1초 | 글리프 수 × 템플릿 수 (NCC 560차원 내적) |
| 바코드 검출 | 0.5~1.5초 | 로케이터 3격자 + 후보별 최대 4단계 디코드 |
| 판정 (`Inspect`) | < 10ms | 단어 수 × 필드 수 선형 |
| 오버레이 렌더 | 0.1~0.3초 | 매치 수 |
| **캐시 히트 시 페이지 전환** | **< 50ms** | 판정 + 렌더만 |

### 21.2 최적화 기법

| 기법 | 대상 | 효과 |
|---|---|---|
| `unsafe` 포인터 휘도 접근 | 모든 화소 연산 | `GetPixel` 대비 수십 배 |
| 서브샘플링 (이형지 2px, 격자 1~2px) | 크롭·로케이터 | 4배 |
| 적응 샘플 간격 | 기울기 추정 | `step = max(2, √(area/60000))` — 면적 무관 일정 비용 |
| 거친→미세 2단계 탐색 | 각도 | 0.1° 전수(121회) → 25+9회 |
| 누적합 (summed-area) | 분리 창 밀도 | O(1) 면적 조회 |
| 2단 캐시 (메모리 LRU + 디스크) | OCR 결과 | 재열기 시 API 호출 0 |
| 우선순위 큐 | 프리페치 | 현재 페이지 체감 지연 제거 |
| `Task.Yield()` 주기 삽입 | 자동 검사 | UI 응답성 유지 |
| 5페이지마다 집계 갱신 | 자동 검사 | 렌더 부하 1/5 |

### 21.3 메모리

```
페이지 비트맵    ≈ W × H × 4 byte   (2384×3370 → 32MB)
렌더 캐시        6장 (60MB 초과 비트맵은 2장)  → 최대 약 200MB
정렬 캐시        max(6, CachePages)장
OCR 캐시(메모리) 500건 × 수 KB      → 수 MB
글리프 라이브러리 문자당 6개 × 2.2KB → 수백 KB
```

---

## 22. 알려진 한계와 설계 트레이드오프

### 22.1 기능적 한계

| 한계 | 원인 | 완화 |
|---|---|---|
| OCR 정확도가 인쇄 품질에 좌우 | OCR 본질 | 저신뢰 하이라이트, 교정 학습, 확인 필요 강제 |
| 문서번호 없는 라벨(중국 등)은 규격 자동 선택 불가 | 감지 근거 부재 | 이전 규격 유지 + 수동 지정 안내 |
| DataMatrix 값 미검증 | 사용자 정책 결정 | 바코드 인식 모드로 클릭 판독 |
| 바코드 등급은 참고값 | 교정 광학계 없음 | 문서에 명시, 전용 장비 대체 불가 |
| 이형지 배경 전제 | 크롭 알고리즘이 하늘색 기준 | 미감지 시 크롭 생략·기울기만 시도 |
| 판정이 페이지 단위 | 설계 선택 | 모아보기 표가 몇 번째 라벨인지 보여줌 |
| 자동 검사 소요 = OCR 속도 | 외부 API 의존 | 캐시·프리페치로 재실행은 빠름 |
| 전자서명·감사추적 없음 | 규제 위치(보조 도구) | 정식 기록은 사내 양식으로 |

### 22.2 설계 트레이드오프

| 선택 | 대안 | 채택 이유 |
|---|---|---|
| 검출 수 **정확히 일치**(`=`) | `≥` | 초과 검출은 오탐·혼입 신호이므로 드러내야 함 |
| GTIN만 **≥1** | 규격 카운트 준수 | 값이 동일하므로 하나면 충분, 인쇄 방식 변화에 강함 |
| 바코드 GTIN 불일치 시 **0건 유지** | OCR 폴백 | 폴백하면 인쇄 텍스트가 맞을 때 거짓 합격 |
| 자동 학습 **기본 꺼짐** | 기본 켬 | 사용자가 모르는 사이 데이터가 쌓이는 것을 방지 |
| 규격을 **라벨에서만** 판별 | 목록 STANDARD 열 | 목록이 틀려도 라벨이 진실, 단일 원천 |
| 판정 서명으로 **중복 저장 차단** | 매번 저장 | 페이지당 판정 1건 원칙 |
| Core를 **GUI 비의존**으로 분리 | 단일 프로젝트 | 판정 로직 전체를 Linux CI에서 테스트 |
| **self-contained** 배포 | framework-dependent | 검사 PC에 런타임 설치 불필요 |

### 22.3 규제상 위치 재확인

자동 판정은 참고값이며 최종 합격·불합격은 검사자가 결정한다. '확인 필요'는 불합격이 아니라
검사자 확인 대기다. 검사자 확인 합격은 사유·처리자·시각과 함께 기록되지만 **전자서명이 아니다.**

> **⚠ 운영 주의** — 검사 성적서·기록지의 판정란이 P(합격)로 기본 입력되는 운영 체계에서는,
> LaVIS가 '확인 필요'로 표시하고 검사자가 확인한 결과 **실제 부적합**이면 기록의 판정란을
> **검사자가 직접 부적합으로 고쳐 적어야 한다.** 프로그램은 기록지를 대신 바꾸지 않는다.

---

## 23. 확장 가이드

### 23.1 새 검사 규격 추가

```
1. %APPDATA%\LaVIS\standards.json 의 "standards"에 항목 추가
   - counts: 필드별 기대 횟수 (GTIN 값은 무시됨)
   - date_format: "%Y-%m-%d" 또는 "%Y.%m.%d"
   - display_name: "XXX-001(Rev.2)" 형태로 쓰면 문서번호 감지가 자동 동작
   - (선택) doc_patterns: 표시명으로 부족할 때 정규식 추가
2. field_colors에 새 필드가 있으면 색 추가
3. 프로그램 재시작 또는 설정 저장
```

`display_name` 명명 규칙만 지키면 `StandardDetector`가 별도 코드 없이 인식한다(§12.1).

### 23.2 새 검출 필드 추가

**내장 필드**는 `BuildSearchTerms`와 `Schema.CanonicalColumns`에 함께 추가해야 한다.
**커스텀 필드**는 코드 수정 없이 설정만으로 가능하다.

```
설정 → 검출 필드·영역 → 커스텀 필드 행 추가
  name     : 표시 이름
  pattern  : 찾을 값 (고정 문자열 또는 정규식)
  is_regex : 정규식 여부 (true면 단어 전체 일치)
  expected : 기대 횟수 (null이면 참고 표시만)
  standard : 이 값이 검출되면 선택할 규격 (선택)
```

### 23.3 새 OCR 엔진 추가

```csharp
public sealed class MyOcrEngine : IOcrEngine
{
    public string Id => "myengine";
    public string DisplayName => "내 엔진";
    public Task<List<OcrWord>> DetectWordsAsync(SKBitmap image, CancellationToken ct = default)
    {
        // 좌표는 반드시 입력 이미지 픽셀 기준
        // 실패는 OcrException으로 — 빈 리스트 반환 금지
    }
}
```

배선 지점 3곳:

```csharp
// ① InspectorView.CurrentOcrEngine()
_config.Section("ocr")["engine"]?.GetValue<string>() switch {
    "pattern" => _glyphEngine, "onnx" => OnnxEngine.Value,
    "myengine" => _myEngine, _ => _textract };
// ② AppConfig.Normalize — 허용 엔진 목록에 "myengine" 추가
// ③ SettingsWindow.xaml — OcrEngineCombo에 항목 추가
```

캐시 키에 `eng=<Id>`가 들어가므로 엔진을 바꾸면 자동으로 재분석된다.

### 23.4 새 교차검증 추가

```csharp
// 1) CrossCheckResult를 만드는 검증 함수 작성 (Core)
public static List<CrossCheckResult> MyCheck(LabelRecord record, PageAnalysis analysis)
    => [ new CrossCheckResult("내검증", "항목명", 실제값, 기대값, 일치여부) ];

// 2) InspectorView.ComputePage 에서 barcodeChecks에 AddRange
barcodeChecks.AddRange(MyCheck(record, analysis));

// 3) 표에 표시하려면 ShowOutcome의 부가 검증 행 필터에 Source 추가
.Where(c => c.Source is "동일값" or "기준DB" or "내검증")
```

`BarcodeChecks`에 들어가면 `Outcome.Passed`에 **자동으로 반영**된다(`All(c => c.Matched)`).

### 23.5 코딩 규약

| 규약 | 근거 |
|---|---|
| Core에 WPF 타입 참조 금지 | Linux 테스트 유지 |
| 색·크기 리터럴 금지 (Theme 토큰만) | `ThemeTokenTests` |
| `MessageBox` 직접 호출 금지 (`Dialogs` 경유) | `AppSourceGuardTests` |
| 모든 Window에 `IsCancel="True"` 버튼 | 소스 가드 |
| 숫자 설정은 `SettingRanges`에 등록 + XAML `Tag` 배선 | 소스 가드 |
| 파괴적 동작 확인은 기본 '아니오' | HFE 규범 |
| 정렬 알고리즘 변경 시 `PreprocessSig` 버전 상승 | 캐시 좌표 오염 방지 |
| 주석은 **왜**를 설명 (무엇은 코드가 말함) | 기존 코드 스타일 |

---

## 부록 A. 설정 키 전체

| 키 | 타입 | 기본값 | 범위 | 설명 |
|---|---|---|---|---|
| `schema_version` | int | 2 | — | 마이그레이션 기준 |
| `last_files.schedule/product/bsc` | string | `""` | — | 목록 생성 최근 파일 |
| `last_list_path` | string | `""` | — | 최근 목록 파일 |
| `save_directory` | string | `""` | 절대 경로 | 빈 값이면 `~/LaVIS_결과` |
| `aws.region` | string | `ap-northeast-2` | — | Textract 리전 |
| `aws.profile` | string | `""` | — | 자격증명 프로필 |
| `shelf_life_months` | int | 36 | 1~120 | 유효기한 계산 |
| `pdf_render_zoom` | double | 4.0 | 1~8 | dpi = 72×zoom |
| `prefetch_policy` | string\|int | `all` | — | 전체 또는 앞 N페이지 |
| `ocr_cache_max_entries` | int | 500 | 50~5000 | 캐시 LRU 한도 |
| `page_image_cache_pages` | int | 6 | 1~20 | 렌더 캐시 |
| `auto_save_default` | bool | false | — | 자동 저장 초기값 |
| `jpeg_quality` | int | 90 | 30~100 | 결과 이미지 |
| `save_scale` | double | 0.5 | 0.1~1.0 | 결과 이미지 축소 |
| `ocr.engine` | string | `aws` | aws/pattern/onnx | |
| `ocr.max_dimension` | int | 2000 | 500~4000 | 전송 최대 변 |
| `ocr.jpeg_quality` | int | 85 | 30~100 | 전송 품질 |
| `ocr.min_confidence` | int | 0 | 0~100 | 0 권장 |
| `ocr.contrast_stretch` | bool | false | — | 대비 보정 |
| `ocr.allow_confusables` | bool | true | — | 혼동 문자 허용 |
| `ocr.quality_alarm` | bool | true | — | 저신뢰 알람 |
| `ocr.low_word_confidence` | int | 70 | 0~100 | 저신뢰 단어 기준 |
| `ocr.low_avg_confidence` | int | 80 | 0~100 | 저신뢰 평균 기준 |
| `overlay.thickness` | int | 2 | 1~12 | 선 굵기 |
| `overlay.fill_alpha` | int | 90 | 0~255 | 채움 불투명도 |
| `overlay.show_numbers` | bool | false | — | 박스 번호 |
| `overlay.show_zones` | bool | true | — | 영역 점선 표시 |
| `overlay.show_gallery` | bool | true | — | 필드 모아보기 |
| `overlay.gallery_percent` | int | 40 | 10~80 | 모아보기 폭 비율 |
| `fields.disabled[]` | string[] | `[]` | — | 검사 제외 (LOT 불가) |
| `fields.custom[]` | object[] | `[]` | — | name/pattern/is_regex/expected/standard |
| `fields.charsets[]` | object[] | 6개 | — | field/allowed/denied |
| `fields.same_value[]` | object[] | `[]` | — | name/pattern/min_instances |
| `fields.zones[]` | object[] | `[]` | — | field/standard/region(%) |
| `preprocess.crop_label` | bool | true | — | 이형지 크롭·기울기 보정 |
| `type_learning.min_samples` | int | 5 | 2~100 | 유형 판정 시작 표본 |
| `learning.glyph_patterns` | bool | **false** | — | OCR 패턴 자동 축적 |
| `learning.label_type` | bool | **false** | — | 유형 학습·이상 알람 |
| `learning.same_value_layout` | bool | **false** | — | 동일값 배치 갱신 |
| `learning.master_db` | bool | **false** | — | 기준정보 자동 축적 |
| `label_forms.rules[]` | object[] | `[]` | — | name/standard/region/text_pattern/use_image |
| `zones.keep_mode` | bool | false | — | 연속 영역 등록 |
| `export.csv_text_protect` | bool | true | — | `="값"` 래핑 |

---

## 부록 B. 주요 수식 모음

```
── 휘도 ──────────────────────────────────────────────
Y = (299·R + 587·G + 114·B) / 1000

── 이진화 문턱 (공통) ─────────────────────────────────
lo = 2% 퍼센타일,  hi = 98% 퍼센타일
T  = (lo + hi) / 2

── 대비 스트레칭 ──────────────────────────────────────
out = clamp((Y − lo) × 255 / (hi − lo), 0, 255)

── 이형지 판정 ────────────────────────────────────────
하늘색 ⟺ B ≥ 110 ∧ B > R + 18 ∧ G + 6 ≥ R

── 기울기 추정 ────────────────────────────────────────
proj_i(θ) = round((x_i·sin θ + y_i·cos θ) / 3)
Score(θ)  = Σ_b count(b)²
채택: Score(best) > Score(0) × 1.03,  0.3° ≤ |θ| ≤ 10°

── 글리프 정규화·매칭 ─────────────────────────────────
ink   = clamp((T×1.3 − Y) / (T×1.3), 0, 1)
v_i   = (p_i − mean(p)) / ‖p − mean(p)‖₂
NCC   = Σ a_i·b_i                       (채택 ≥ 0.55, 중복 > 0.985)
배제: aspect비 > 1.8 ∨ 높이비 > 1.9 ∨ (둘 다 소형 ∧ |ΔVOffset| > 0.28)

── DataMatrix 후보 ───────────────────────────────────
dark(cell) ⟺ 어두운 화소 비율 ≥ DarkRatio(0.28 / 0.42 / 0.30)
Score = fill × min(aspect, 1/aspect)
분리 창: fill ≥ 0.8,  Score = fill × (1 − RingDark)
L 파인더 ⟺ (left ≥ .85 ∨ right ≥ .85) ∧ (top ≥ .85 ∨ bottom ≥ .85)
L 유도 정사각형: |rowLen − colLen| / max ≤ 0.15

── 바코드 등급 ────────────────────────────────────────
SC  = (Rmax − Rmin) / 255
MOD = clamp((mean(light) − mean(dark)) / (Rmax − Rmin), 0, 1)
DEF = |{v : |v − T| < (Rmax−Rmin)×0.125}| / N
Score = min(SC점수, MOD점수, DEF점수)
Letter: ≥3.5 A, ≥2.5 B, ≥1.5 C, ≥0.5 D, else F

── LOT 유사도 ────────────────────────────────────────
ratio(a,b) = 2M / (|a| + |b|)                    (Ratcliff/Obershelp)
Score = (0.40·ratio + 0.30·prefix + 0.20·conf + 0.10·length) × 100

── 영역 필터 ─────────────────────────────────────────
cx = (X + W/2)/pageW,  cy = (Y + H/2)/pageH
인정 ⟺ ∃z: cx ∈ [z.X − .04, z.X + z.W + .04] ∧ cy ∈ [z.Y − .04, z.Y + z.H + .04]

── 판정 ─────────────────────────────────────────────
Gating = Expected > 0 ∧ Term ≠ ""
Passed_field   = ¬Gating ∨ (AtLeastOne ? Found ≥ 1 : Found = Expected)
Passed_outcome = ∀f Passed_field ∧ ∀c c.Matched
FinalPassed    = InspectorVerdict?.Passed ?? Passed_outcome

── 판정 서명 ─────────────────────────────────────────
SHA1( [규격,LOT,REF,P|C] ⧺ sort(필드) ⧺ sort(바코드) ⧺ [검색어] )[0..16]

── 동일값 드리프트 ───────────────────────────────────
drift = (median{n(r).x − r.x}, median{n(r).y − r.y}),  dist ≤ 0.2
누락 ⟺ min dist(instance, r + drift) > 0.08

── 양식 이미지 매칭 ──────────────────────────────────
score = Σ template_i × current_i         (64×48 zero-mean unit-norm, 채택 ≥ 0.75)
합성: 텍스트∧이미지 → 0.5 + score/2,  텍스트만 → 0.95

── OCR 품질 ─────────────────────────────────────────
IsPoor ⟺ mean(conf) < 80 ∨ (|{conf < 70}| / N) > 0.3

── 유형 학습 ─────────────────────────────────────────
static = {t : count(t)/samples ≥ 0.8}
IsAnomaly ⟺ |static \ current| > 0 ∨ |current \ known| ≥ 3
IsWordLike(t) ⟺ |t| ≥ 3 ∧ 영숫자 ≥ 0.6|t| ∧ 글자 ≥ 2

── 유효기한 ─────────────────────────────────────────
EXP = MFG.AddMonths(shelf_life_months).AddDays(−1)
```

---

## 부록 C. 용어집

| 용어 | 정의 |
|---|---|
| **AI** | Application Identifier — GS1 응용식별자. 01=GTIN, 10=LOT, 11=제조일, 17=유효기한 |
| **Ambiguous** | 문서번호는 읽었으나 Rev를 못 읽어 규격 후보가 여럿인 상태. 이전 규격 유지 |
| **AtLeastOne** | 1개 이상 일치면 합격인 필드 속성. GTIN 전용 |
| **BarcodeChecks** | 교차검증 행 목록. GS1 대조 + LOT 미매칭 + 기준DB + 동일값. 하나라도 불일치면 확인 필요 |
| **ExtractionFailed** | GTIN을 바코드·OCR 어느 경로로도 뽑지 못한 오류 상태 |
| **FNC1** | GS1 구분자(0x1D, GS). 가변장 AI 값의 종료 표시 |
| **Gating** | 판정에 영향을 주는 필드 (기대 횟수 > 0 ∧ 검사 값 있음) |
| **InspectorVerdict** | 검사자 확인 합격 기록 (사유·처리자·시각). 자동 판정은 보존 |
| **L 파인더** | DataMatrix의 인접한 두 변 실선. 심볼 식별 특징 |
| **LinerStats** | 하늘색 이형지 픽셀 통계 (개수·바운딩 박스) |
| **NCC** | Normalized Cross-Correlation. zero-mean unit-norm 벡터의 내적 |
| **PageAnalysis** | 페이지 1장의 OCR 단어 + 바코드 목록. 캐시 단위 |
| **PreprocessSig** | 전처리 알고리즘 버전 서명. 캐시 키에 포함 (`crop-v2`) |
| **RingDark** | 후보 창 둘레 1칸 띠의 어두운 비율. 콰이엇 존 판별 |
| **Signature** | 판정 내용의 SHA-1 앞 16자. 중복 저장 방지·처리 유효성 |
| **Zone (영역)** | 필드 검출을 인정할 페이지 비율 사각형 |
| **검사 보조 도구** | 최종 판정 주체가 검사자인 도구. 21 CFR Part 11 대상 아님 |
| **관대 파싱** | 미등록 AI를 건너뛰고 등록 AI만 해석하는 GS1 모드 |
| **문서번호** | 라벨에 인쇄된 양식 번호·개정. 규격 자동 선택의 유일한 근거 |
| **세대(Generation)** | 프리페치 워커의 문서 버전 태그. 교체 시 잔여 잡 폐기 |
| **소유 렌더** | 호출자가 Dispose 책임을 지는 비트맵. 분석 스레드 전용 |
| **확인 필요(CHECK)** | 불합격이 아니라 검사자 확인 대기 상태 |

---

*이 문서는 구현 코드에서 직접 도출되었으며, 인용된 모든 코드·수식은 작성 시점의 실제 구현과 일치합니다.
코드를 수정할 때 이 문서의 해당 절도 함께 갱신하세요.*
