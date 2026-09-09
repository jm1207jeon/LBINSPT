# LaVIS 패밀리 디자인 규범 (UDInspect 공통)

LaVIS와 UDInspect(2D-Verifier)는 같은 회사·같은 검사실에서 쓰는 자매 제품이다. 검사자가
두 프로그램을 오갈 때 같은 규칙으로 읽히도록, UDInspect가 6주간 현장 피드백으로 확정한
시각·조작 규범을 LaVIS에 그대로 적용한다. 토큰의 단일 출처는
`csharp/src/LabelSuite.App/Theme.xaml`이다.

## 1. 원칙

- 커스텀 컨트롤 템플릿을 쓰지 않는다. 기본 WPF 크롬 위에 **색·굵기·크기·여백**만으로 위계를 만든다.
- 색은 "의미"에만 쓴다. 파랑=주 동작/선택, 주황=집계·확인 필요, 빨강=파괴적/오류, 녹색=합격/저장됨.
- 되돌리기 어려운 동작은 ①색으로 분리 ②확인 대화상자 ③기본 버튼 '아니오'의 3중 잠금.
- 상태는 상태바 한 곳이 아니라 **상시 표시**(미저장·진행률·엔진 상태)로 보여 준다.
- 조작법은 기억이 아니라 화면에 둔다: GroupBox 헤더 괄호, 툴팁(효과+부작용+되돌림 가능 여부).

## 2. 팔레트

| 토큰 | 값 | 용도 |
|---|---|---|
| `PrimaryBrush` | `#1A6FB5` | 주 동작 버튼 배경, 선택 테두리, 진행 집계 2순위, 아이콘 돋보기 |
| `AccentBrush` | `#1A5276` | 정보성 강조 **글씨** 전용 (배경으로 쓰지 않음) |
| `PrimarySoftBrush` | `#E3F0FB` | 선택 카드 배경, 칩 |
| `SoftPanelBrush` / `SoftPanelBorderBrush` | `#F3F6FA` / `#B9C4D0` | 연파랑 상태 패널 (CornerRadius 6) |
| `GroupAltBrush` | `#E8F1FA` | 같은 묶음(LOT 등) 교대 배경 |
| `CountOrangeBrush` | `#E67E00` | 큰 숫자 집계 1순위 (확인 필요) |
| `SuccessBrush` / `SuccessBgBrush` | `#1E8449` / `#D5F5E3` | 합격 글씨 / 합격 행 |
| `ScannedBgBrush` | `#D7EFD7` | 확인된 칸 채움 |
| `FailBgBrush` / `FailFgBrush` | `#FFC7CE` / `#9C0006` | 부적합·불일치·만료 행 |
| `DangerButtonBrush` | `#D9534F` | 파괴적 버튼 (삭제·초기화) |
| `CautionButtonBrush` | `#F0AD4E` | 주의 버튼 (선택 행 삭제) |
| `StatusWarnBrush` / `StatusErrorBrush` | `#9A5B00` / `#B01E1E` | 상태바 경고 / 오류 |
| `SavedGreenBrush` | `#2E7D32` | `✓ 저장됨` |
| `InvalidInputBrush` | `#FFE4E1` | 잘못된 입력란 배경 |
| `ReadoutBrush` / `ReadoutBorderBrush` | `#FDF6E3` / `#BBB69A` | 판정 배지(크림) |
| `ImageBgBrush` / `ImageHintBrush` | `#1E1E1E` / `#9E9E9E` | 이미지 영역 / 빈 상태 안내 |
| `HintBrush` | `#666666` | 안내문 |
| `GridLineBrush` | `#DDDDDD` | 표 격자선 |
| `OverlayDimBrush` | `#66000000` | 메인 창 안 오버레이 딤 |
| `InkBrush` / `CheckBrush` | `#222B36` / `#2E9E4F` | 아이콘 잉크 / 체크 |

## 3. 타이포·간격

- 글꼴 미지정(시스템 기본). 크기 계층: 큰 숫자 집계 48~56 / 판독값 24 / 배지 17 / 집계 라벨 14 / 표 13 / 안내 12 / 캡션 11.
- 판정 배지의 24px 헤드라인은 판정어만(`✓ 합격 (PASSED)` · `⚠ 확인 필요 (CHECK)`) 한 줄 — 규격·사유는 11px 사유줄로 내리고,
  판정이 아닌 안내(OCR 실패·목록 없음)는 15px 한 줄로 낮춘다. 두 줄 모두 `TextTrimming=CharacterEllipsis` + 툴팁 전문(잘림 금지).
- Button Padding 10,5 · Margin 2 / GroupBox Margin 4 · Padding 6 / 설정 창 바깥 여백 10, 하단 버튼줄 상단 10.
- CornerRadius 3단계: 칩 4 / 패널 6 / 오버레이 카드 10.
- 탭 헤더는 `"  두 칸 여백  "` 관습.

## 4. 컴포넌트 규칙

| 상황 | 규칙 | 스타일 키 |
|---|---|---|
| 주 동작 (화면당 1~2개) | 파랑 배경 흰 굵은 글씨 | `PrimaryButton` |
| 보조 강조 | 네이비 굵은 글씨, 기본 크롬 | `AccentButton` |
| 파괴적 동작 | 빨강, 다른 버튼과 분리 배치, 확인 대화상자 기본 '아니오', 건수 표시 | `DangerButton` |
| 주의 동작 | 주황, 파괴 버튼 왼쪽 | `CautionButton` |
| 규격·양식 선택 | 카드형 토글: 보통 흰/회색 2px, 선택 파랑 3px + 연파랑, 글자색 유지 | `StandardToggle` |
| 안내문 | 12px #666666, 줄바꿈 | `HintText` |
| 판정 배지 | 크림 박스 | `BadgeBorder` |
| 경고 배너 | 본문 상단 주황 테두리 | `AlertBanner` |
| 오버레이 | 딤 + 카드(#F4F6F9, 파랑 2px, R10, 그림자) | `OverlayCard` |
| 표 | 전체 격자 #DDDDDD, 행 24, 글꼴 13, 행 헤더 없음, 숫자 열 우측 정렬 | (기본 DataGrid), `RightCell` |

## 5. 상태 표시 규범

- **상태바 3단계**: `[HH:mm:ss]` 접두 + 정보(검정) / 경고(갈색 굵게) / 오류(빨강 굵게). 말줄임 + 툴팁으로 전체 보기.
  경고·오류는 5초간 고정되어 뒤따르는 정보 메시지에 덮이지 않는다. 경고·오류는 `%APPDATA%\LaVIS\app.log`에 남는다.
- **미저장 상시 표시**: 집계 줄에 `● 결과 미저장 N건`(빨강) / `✓ 결과 저장됨`(녹색). 미저장 상태로 종료하면 확인 대화상자(기본 '아니오').
- **진행률**: 상태바 우측 진행 막대 + `OCR n/N`.
- **엔진 상태**: 상태바 좌측 `OCR: …` 굵게 (녹색=준비, 빨강=인증 실패).
- **툴바 옵션 즉시 저장**: 자주 바꾸는 옵션(자동 저장 등)은 바꾸는 즉시 설정에 저장한다. 구조적 설정은 설정 창의 [저장]으로 확정한다.

## 6. 아이콘

`docs/lavis_icon.svg`(원본) → `Assets/app.ico`. UDInspect와 같은 구성 규칙 "검사 대상 + 파랑 돋보기 + 초록 체크".
UDInspect는 대상이 DataMatrix 격자, LaVIS는 라벨 카드(필드 행 + 바코드 띠). 렌즈 좌표·색·굵기는 동일하다.

## 7. 창별 적용 명세

| 창 | 주 동작(파랑) | 보조 | 파괴·주의 | 상태 표시 | Esc / Enter | 최소 크기·작업표시줄 |
|---|---|---|---|---|---|---|
| MainWindow | (탭 안에서 결정) | ⚙ 설정(상태바) | — | 상태바 3단계 · OCR 진행 막대 · 엔진 상태 · 버전 | Esc: About 닫기 | 1280×720 |
| InspectorView | PDF 열기 · 결과 저장 (2개 허용, §8) | 저장 경로 · CSV 내보내기 · OCR 로그 · 양식 학습 | 없음(삭제 동작 없음) | 큰 숫자 집계 · `● 결과 미저장` · 판정 배지 플래시 · LOT 미매칭 경고 | Esc: 영역 등록 모드 해제 | — |
| GeneratorView | 리스트 생성 | 검사 탭으로 보내기(네이비) · 엑셀로 저장 | — | 경고/오류 패널 · 상태바 | — | — |
| HistoryView | LOT 리포트 내보내기 | 기준정보 DB · 새로고침 | — | 새 행 앰버 플래시 · 상태바 | — | — |
| SettingsWindow | 저장 | 설정 폴더 열기 · 인증 확인(네이비) · 프리셋 내보내기 | 패턴 초기화(빨강, 우측 분리) · 프리셋 가져오기(주황) | 하단 안내문 · 무효 입력 붉은 배경 | Esc: 취소 | 760×560 · 숨김 |
| OcrLogWindow | 선택 단어로 교정 등록 | 선택 병합 학습(네이비) | — | 저신뢰 행 색 · 건수 | Esc: 닫기(변경 있으면 재검사) | 560×420 · 숨김 |
| CorrectionDialog | 교정 등록(Enter) | — | — | — | Esc: 닫기 | 420×380 · 숨김 |
| MasterDbWindow | 저장 | — | 선택 행 삭제(주황, 왼쪽 분리) | 하단 저장 안내 | Esc: 닫기 | 640×380 · 숨김 |
| InputDialog | 확인(Enter) | — | — | 안내문 | Esc: 취소 | 460 · 숨김 |
| ChoiceDialog | 동작을 말하는 버튼 1~2개 | — | 주황=되돌리기 어려움/과금 | — | Esc: 취소(-1) | 520 · 숨김 |
| About 오버레이 | 닫기 | 데이터 폴더 · 로그 열기 | — | 버전·빌드·경로 | Esc/딤 클릭: 닫기 | 메인 창 안 |

## 8. 허용 편차와 근거

- **⚙ 설정이 상태바에 있음**: 탭 3개가 각자 툴바를 가져 공통 위치가 상태바뿐이다. UDInspect는 툴바에 둔다.
- **판정색 녹/주황 유지**: UDInspect 집계는 주황(1순위)/파랑(2순위)이지만 LaVIS의 집계는 판정 결과(합격/확인 필요)라 의미색을 따른다.
- **주 동작 파랑 2개(PDF 열기·결과 저장)**: 검사 흐름의 시작과 끝을 각각 표시. 나머지 버튼은 기본 크롬.
- **뷰어 줌·팬·영역 등록**: LaVIS 고유. 러버밴드는 UDInspect 강제 스캔 박스와 같은 주황 반투명.
- **MinWidth 1280**: UDInspect와 동일 (이전 1180).

## 9. 색–의미 1:1 표

| 색 | 의미 | 쓰이는 곳 | 형태 단서 |
|---|---|---|---|
| 파랑 `#1A6FB5` | 주 동작 · 선택 · 진행 | PrimaryButton, 선택 카드 테두리, 현재 페이지 슬롯 | 채움/굵은 테두리 |
| 네이비 `#1A5276` | 정보성 강조 글씨 | 파일명, AccentButton | 글씨만 |
| 녹색 `#1E8449` / `#D5F5E3` | 합격 · 저장됨 | 배지, 행 배경, `✓` | 기호 ✓ |
| 주황 `#CA6F1E` / `#FDEBD0` | 확인 필요(검사자 확인 대기) | 배지, 행 배경, 집계 | 기호 ! |
| 주황 `#F0AD4E` | 되돌리기 어려운 동작 | CautionButton | 버튼 |
| 주황 파선 `#FF8C00` | 저신뢰 OCR 단어 | 오버레이 | 파선 |
| 주황 반투명 `#96FF9800` | 영역 등록 러버밴드 | 뷰어 | 점선 사각형 |
| 빨강 `#D9534F` | 파괴적 동작 | DangerButton | 분리 배치 |
| 빨강 `#B01E1E` | 오류 · 미저장 | 상태바, `● 미저장` | 굵게 |
| 부적합 `#FFC7CE`/`#9C0006` | 불일치 · 만료 | 바코드 검증 행 | 굵게 |
| 청록 `#008080` | 바코드 박스 | 오버레이 | 라벨 텍스트 |
| 보라 `#8000A0` | 양식 감지 영역 | 오버레이 | 라벨 텍스트 |

WPF 토큰 ↔ Core SKColor 동기화: `OverlayBarcodeBrush` = `Annotate.BarcodeBoxColor`, `OverlayFormBrush` = `Annotate.FormBoxColor`,
`OverlayLowConfBrush` = `Annotate.LowConfidenceColor` (ThemeTokenTests가 hex 일치를 검증).

## 10. UDInspect 역이식 치환표

| UDInspect 인라인 값 | LaVIS 토큰 |
|---|---|
| `#FF1A6FB5` | `PrimaryBrush` |
| `#FF1A5276` | `AccentBrush` |
| `#FFF3F6FA` / `#FFB9C4D0` | `SoftPanelBrush` / `SoftPanelBorderBrush` |
| `#FFE3F0FB` | `PrimarySoftBrush` |
| `#FFE8F1FA` | `GroupAltBrush` |
| `#FFFFC7CE` / `#FF9C0006` | `FailBgBrush` / `FailFgBrush` |
| `#FFD7EFD7` | `ScannedBgBrush` |
| `#FFD9534F` / `#FFF0AD4E` | `DangerButtonBrush` / `CautionButtonBrush` |
| `#FFE67E00` | `CountOrangeBrush` |
| `#FFFDF6E3` / `#FFBBB69A` | `ReadoutBrush` / `ReadoutBorderBrush` |
| `#FF1E1E1E` / `#FF9E9E9E` | `ImageBgBrush` / `ImageHintBrush` |
| `#FF666666` | `HintBrush` |
| `#FFDDDDDD` | `GridLineBrush` |
| `(0x9A,0x5B,0x00)` / `(0xB0,0x1E,0x1E)` / `(0x2E,0x7D,0x32)` | `StatusWarnBrush` / `StatusErrorBrush` / `SavedGreenBrush` |
| `(255,193,7)` / `(255,152,0)` | `FlashAmberColor` / `FlashOrangeColor` |
| `#66000000` | `OverlayDimBrush` |
| `(255,228,225)` | `InvalidInputBrush` |

UDInspect에 Theme.xaml을 도입하려면: `App.xaml`의 `MergedDictionaries`에 `Theme.xaml`을 추가하고, 위 표의 인라인 값을
`{StaticResource 키}`로 치환한 뒤, `SetStatus`의 색 상수를 `StatusWarnBrush/StatusErrorBrush`로 바꾼다. (작업 #5 역방향 라운드)

## 11. 리눅스 정합 검사

WPF는 리눅스에서 컴파일되지 않으므로, 다음 소스 규약 테스트가 CI(리눅스)에서 화면 코드를 검사한다.

- `ThemeTokenTests`: XAML/C#이 참조하는 리소스 키가 Theme.xaml에 전부 존재, 화면 XAML에 인라인 hex 색 없음, 오버레이 색이 Core 상수와 일치.
- `AppSourceGuardTests`: 뷰 상태 헬퍼 자기 재귀 없음, `MessageBox.Show`는 Dialogs를 통해서만, 모든 모달 창에 Esc 취소 버튼, 설정 XAML Tag ↔ SettingRanges 양방향 일치.
