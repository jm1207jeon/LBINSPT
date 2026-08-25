# LaVIS — 라벨 검증·검사 시스템

**LaVIS** (Label Verification & Inspection System, 구 LabelSuite)

기존 두 프로그램(`Label Inspector_list generator`의 목록 생성, `Label Inspector`의
라벨 OCR 검사)을 하나의 앱으로 통합·고도화한 **C# (.NET 8 + WPF)** 프로그램입니다.

> 초기 개발에 쓰였던 Python 참조 구현(labelsuite/)과 레거시 원본 폴더는
> C# 전환 완료 후 저장소에서 제거되었습니다. 필요하면 git 이력
> (`89a0d4a` 이전 커밋)에서 복원할 수 있습니다.

## 주요 기능

| 탭 | 기능 |
|---|---|
| **목록 생성** | 주문일정/품목번호/BSC 엑셀 3종 → 검사 목록 생성. 연/월/일 체크박스 날짜 선택, 행 단위 경고/오류 패널, 생성 즉시 검사 탭으로 전달 |
| **라벨 검사** | 라벨 PDF OCR(3엔진: AWS Textract / 패턴 학습·로컬 / ONNX·로컬) + 바코드(DataMatrix·GS1-128) 검사. 전 페이지 선행 OCR 프리페치로 페이지 이동 무지연, LOT·규격 자동 매칭(수동 우선), 라벨 정렬(이형지 크롭·기울기 보정), 필드 영역 드래그 등록, 동일값 패턴, 라벨 양식 감지, 유형 학습·이상 알람, OCR 인식 로그·오인식 교정 학습, 저신뢰 알람, GS1 교차 검증·기준정보 DB·손상 등급, 합불 판정과 주석 이미지 저장 |
| **검사 이력** | SQLite 이력 조회/필터, LOT별 리포트(xlsx) 내보내기 |

## 빌드 / 테스트 / 실행

```bash
# 테스트 (Windows 또는 Linux — 150건)
dotnet test csharp/tests/LabelSuite.Core.Tests
# 실행 (Windows)
dotnet run --project csharp/src/LabelSuite.App
# 폴더 배포 (Windows) — LaVIS/ 폴더(exe + 런타임 DLL + models/) 생성
dotnet publish csharp/src/LabelSuite.App -c Release -r win-x64 --self-contained \
  -o publish/LaVIS
```

## Windows exe 받기 (GitHub Actions — Windows 머신 불필요)

- **Actions 탭 → "Build LaVIS Windows EXE (C#)" → Run workflow** 실행 후,
  완료된 잡의 Artifacts에서 `LaVIS-*.zip`을 내려받습니다.
- 또는 `cs-v1.0.0` 형식 태그를 푸시하면 GitHub Release에 zip이 첨부됩니다.
- zip 압축 해제 → `LaVIS` 폴더의 `LaVIS.exe` 실행 (폴더 안 README.txt 참고).

배포 폴더 구성: `LaVIS.exe`(본체) + `*.dll`(런타임) + `models/`(로컬 ONNX OCR
모델) + `README.txt`. 사용자 데이터(설정·학습 패턴·교정 사전·검사 이력·캐시)는
`%APPDATA%\LaVIS`에 분리 저장되므로 (구 LabelSuite 데이터는 첫 실행 시 자동
이전) 폴더를 새 버전으로 통째로 교체해도 유지됩니다. 학습 데이터는
설정 → OCR 설정 → [학습 데이터 내보내기/가져오기]로 `.lslearn` 파일 하나로
다른 PC에 이식할 수 있습니다.

## 구조

```
csharp/
├── src/LabelSuite.Core/   # GUI 비의존 순수 로직 (스키마·생성·검사·OCR·바코드·이력)
├── src/LabelSuite.App/    # WPF GUI (검사/목록/이력 탭, 설정 창, 뷰어)
└── tests/                 # xunit 150건 (합성 렌더링 OCR 왕복 포함)
docs/                      # 워크플로 분석·비교 보고서·체크리스트
.github/workflows/         # 테스트 → 빌드 → 폴더 패키징 → 릴리스
```

고급 설정(컬럼 매핑 · 중국 REF 매핑 · 규격 표시명)은 설정 창의
"설정 폴더 열기"로 JSON을 직접 편집합니다.

## 검사 목록 파일 계약

- 시트 `Label Inspection List`, 헤더:
  `LOT, PRODUCTS, PN, REF, MFG DATE, EXP DATE, GTIN[, STANDARD]`
- 구버전 7컬럼 파일도 읽을 수 있으며, GTIN은 로드 시 GTIN-14로 정규화됩니다.
- `STANDARD` 값(MDR/MDD/BSC/A00/A02/중국)이 있으면 검사 탭에서 규격이 자동
  선택됩니다.
