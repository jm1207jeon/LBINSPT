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

## 규제상 위치

LaVIS는 **검사 보조 도구**입니다. 프로그램은 검출 결과와 참고 판정('합격' / '확인 필요')을
표시하며, 최종 합격·불합격은 검사자가 결정합니다. '확인 필요'는 불합격이 아니라 검사자
확인 대기입니다. 전자서명·감사추적(21 CFR Part 11) 대상 시스템이 아니며, 자매 제품
UDInspect와 같은 간이 검증 트랙(위험 낮음)을 전제로 합니다.

## 검사 규칙 요약

| 항목 | 규칙 |
|---|---|
| 기본 검출 필드 | LOT / PN / REF / MFG DATE / EXP DATE / GTIN (중국 규격은 CHINA 추가). PRODUCTS는 규격이 기대 횟수를 명시할 때만 |
| GTIN 판정 원천 | ① GS1 바코드 AI(01) 값 → ② 페이지에 GS1 바코드가 전혀 없을 때만 OCR `(01)+14자리`. 바코드가 있는데 GS1 구조를 해석하지 못하면 '확인 필요' |
| LOT 매칭 | 라벨에서 목록의 LOT을 못 읽으면 `⚠ 미매칭` 경고 + '확인 필요' 강제 (이전 LOT으로 검사되는 거짓 합격 방지). 수동 선택은 자동 매칭보다 우선 |
| 판정 | 모든 게이트 필드 검출 수 = 기대 수, 모든 바코드 교차검증 일치 |
| 미저장 보호 | `● 결과 미저장 N건` 상시 표시, 미저장 종료·PDF 교체 시 확인(기본 '아니오') |

## 커스터마이징

- **설정 창**(⚙): 필드 제외·커스텀 필드(정규식)·필드 영역(드래그 등록)·문자 제약·동일값 패턴·
  라벨 양식 감지·규격별 기대 횟수·색상·OCR 엔진/임계·저신뢰 알람·저장 옵션.
- **프리셋**(`.lavispreset`): 규격 + 필드 규칙 + 영역 + 양식 감지 + 컬럼 매핑 + 학습 데이터를 한
  파일로 내보내기/가져오기 (제품군·라인·PC 간 이식, 가져오기 전 자동 백업). 학습 데이터만은 `.lslearn`.
- **설정 안전장치**: 원자적 저장, 스키마 버전 이전, 범위 밖 값 자동 보정(시작 시 안내), 손상 파일 복구.
- **CSV 내보내기**: LOT·GTIN 등은 `="값"`으로 감싸 엑셀의 지수 표기·선행 0 소실을 막습니다.
  외부 도구(pandas 등)에서 읽을 때는 `="…"` 래핑을 벗겨 쓰세요(설정에서 끌 수 있음).

## 빌드 / 테스트 / 실행

```bash
# 테스트 (Windows 또는 Linux — 263건)
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
설정 → OCR 엔진·교정 → [학습 데이터 내보내기/가져오기]로 `.lslearn` 파일 하나로,
규격·규칙·영역·양식까지 통째로는 설정 → 규격·양식 감지 → [프리셋 내보내기/가져오기]로
다른 PC에 이식할 수 있습니다. 배포 README.txt에는 빌드 버전과 exe SHA-256이 기록됩니다.

## 구조

```
csharp/
├── src/LabelSuite.Core/   # GUI 비의존 순수 로직 (스키마·생성·검사·OCR·바코드·이력)
├── src/LabelSuite.App/    # WPF GUI (검사/목록/이력 탭, 설정 창, 뷰어)
└── tests/                 # xunit 263건 (합성 렌더링 OCR 왕복·소스 규약·테마 정합 포함)
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
