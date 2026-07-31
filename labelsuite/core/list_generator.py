"""검사 목록 생성 코어 — 레거시 LiGen(label_inspector_ligen.py)의 비즈니스 규칙을
GUI 없이 순수 함수로 이식. 다음 결함을 수정한다:

- 2/29 제조일: replace(year+3) ValueError → relativedelta로 안전 계산
- 행 단위 오류 무단 소멸(except: continue) → RowIssue로 수집·보고
- REF 날짜 오인 휴리스틱(':' 포함/40000 초과면 무조건 제외) → 앵커된 패턴만 날짜 취급
- 일본 GTIN BSC 미매칭 시 빈 값 → 품목리스트 폴백 + 경고
- GTIN 13자리 패딩 → normalize_gtin14(GTIN-14) 단일 규칙
- 국가 정보를 STANDARD 컬럼으로 출력해 검사 단계의 규격 자동 선택 지원
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta

import pandas as pd
from dateutil.relativedelta import relativedelta

from labelsuite.core.schema import (
    LabelRecord,
    SchemaError,
    date_format_for,
    normalize_gtin14,
)


@dataclass(frozen=True)
class SheetSpec:
    sheet: str
    columns: dict[str, int]

    def col(self, name: str) -> int:
        return self.columns[name]


@dataclass(frozen=True)
class ColumnMaps:
    schedule: SheetSpec
    product: SheetSpec
    bsc: SheetSpec

    @classmethod
    def from_config(cls, raw: dict) -> "ColumnMaps":
        def spec(key: str) -> SheetSpec:
            entry = raw[key]
            return SheetSpec(sheet=entry["sheet"],
                             columns={k: int(v) for k, v in entry["columns"].items()})
        return cls(schedule=spec("schedule"), product=spec("product"), bsc=spec("bsc"))


@dataclass
class RowIssue:
    row_index: int          # 엑셀 기준 행 번호(1-기반, 헤더 포함)
    lot: str
    severity: str           # 'warning' | 'error'
    message: str


@dataclass
class GenerationResult:
    records: list[LabelRecord] = field(default_factory=list)
    issues: list[RowIssue] = field(default_factory=list)
    selected_dates: list[date] = field(default_factory=list)

    @property
    def warning_count(self) -> int:
        return sum(1 for i in self.issues if i.severity == "warning")

    @property
    def error_count(self) -> int:
        return sum(1 for i in self.issues if i.severity == "error")


def load_input_frame(path: str, spec: SheetSpec) -> pd.DataFrame:
    """입력 엑셀 로드. 레거시와 동일하게 첫 행을 헤더로 소비한다."""
    return pd.read_excel(path, sheet_name=spec.sheet)


def _coerce_date(value: object) -> date | None:
    if pd.isna(value):
        return None
    try:
        return pd.to_datetime(value).date()
    except (ValueError, TypeError):
        return None


def extract_available_dates(schedule_df: pd.DataFrame, colmaps: ColumnMaps) -> list[date]:
    """스케줄 MFG DATE 열에서 중복 제거·정렬된 날짜 목록."""
    column = schedule_df.iloc[:, colmaps.schedule.col("mfg")]
    dates = {d for d in (_coerce_date(v) for v in column.dropna()) if d is not None}
    return sorted(dates)


def compute_exp_date(mfg: date, shelf_life_months: int = 36) -> date:
    """유효기한 = 제조일 + 유효기간 - 1일. relativedelta가 2/29를 월말로 보정한다."""
    return mfg + relativedelta(months=shelf_life_months) - timedelta(days=1)


# 셀 전체가 날짜(시각 선택 포함)로만 이루어진 경우만 매칭 — 부분 일치 금지
_DATE_ONLY_PATTERNS = [
    r"\d{4}-\d{1,2}-\d{1,2}",
    r"\d{1,2}/\d{1,2}/\d{4}",
    r"\d{4}/\d{1,2}/\d{1,2}",
    r"\d{1,2}-\d{1,2}-\d{4}",
    r"\d{4}\.\d{1,2}\.\d{1,2}",
    r"\d{1,2}\.\d{1,2}\.\d{4}",
]
_DATE_ONLY_RE = re.compile(
    r"^(?:" + "|".join(_DATE_ONLY_PATTERNS) + r")(?:[ T]\d{2}:\d{2}(?::\d{2})?)?$"
)


def clean_ref(raw: object) -> tuple[str, str | None]:
    """REF 셀 정리. 반환: (ref, 경고 또는 None).

    날짜로 판정하는 경우에만 REF를 비운다:
    - 값 자체가 datetime/Timestamp 인스턴스
    - 문자열 전체가 날짜(+시각) 패턴에 매칭
    - 숫자 타입이면서 엑셀 날짜 시리얼 범위(40000~60000)의 정수
    레거시의 "':' 포함 시 무조건 제외"와 "문자열 float>40000 제외"는 정상 REF를
    지우는 오판정이므로 제거했다.
    """
    if pd.isna(raw):
        return "", None
    if isinstance(raw, (datetime, pd.Timestamp)):
        return "", f"REF 셀이 날짜 값({raw})이라 비웠습니다."
    if isinstance(raw, (int, float)) and not isinstance(raw, bool):
        if float(raw).is_integer() and 40000 <= int(raw) <= 60000:
            return "", f"REF 셀이 엑셀 날짜 시리얼({int(raw)})로 보여 비웠습니다."
        text = format(int(raw), "d") if float(raw).is_integer() else str(raw)
        return text, None
    text = str(raw).strip()
    if not text or text in ("nan", "NaT"):
        return "", None
    if _DATE_ONLY_RE.match(text):
        return "", f"REF 셀이 날짜 문자열({text!r})이라 비웠습니다."
    return text, None


def _standard_for_country(country: str, country_standard_map: dict[str, str]) -> str | None:
    if country in country_standard_map:
        return country_standard_map[country] or None
    return country_standard_map.get("*") or None


def generate_list(
    schedule_df: pd.DataFrame,
    product_df: pd.DataFrame | None,
    bsc_df: pd.DataFrame | None,
    selected_dates: set[date] | list[date],
    colmaps: ColumnMaps,
    country_standard_map: dict[str, str] | None = None,
    shelf_life_months: int = 36,
) -> GenerationResult:
    """선택 날짜의 스케줄 행으로 검사 목록을 생성한다."""
    country_standard_map = country_standard_map or {}
    wanted = set(selected_dates)
    result = GenerationResult(selected_dates=sorted(wanted))
    if not wanted:
        return result

    sched = colmaps.schedule.columns

    # 조회 테이블 사전 구축 (레거시는 행마다 DataFrame 필터를 2회씩 수행)
    product_gtin_by_pn: dict[object, object] = {}
    if product_df is not None:
        pcols = colmaps.product.columns
        for _, prow in product_df.iterrows():
            pn_val = prow.iloc[pcols["pn"]]
            if pd.notna(pn_val) and pn_val not in product_gtin_by_pn:
                product_gtin_by_pn[pn_val] = prow.iloc[pcols["gtin"]]

    bsc_by_pn: dict[object, tuple[object, object]] = {}
    if bsc_df is not None:
        bcols = colmaps.bsc.columns
        for _, brow in bsc_df.iterrows():
            pn_val = brow.iloc[bcols["pn"]]
            if pd.notna(pn_val) and pn_val not in bsc_by_pn:
                bsc_by_pn[pn_val] = (brow.iloc[bcols["ref"]], brow.iloc[bcols["gtin"]])

    grouped: dict[date, list[LabelRecord]] = {d: [] for d in sorted(wanted)}

    for idx, row in schedule_df.iterrows():
        excel_row = idx + 2  # 헤더 1행 + 1-기반
        try:
            mfg = _coerce_date(row.iloc[sched["mfg"]])
            if mfg is None or mfg not in wanted:
                continue

            lot = str(row.iloc[sched["lot"]]).strip() if pd.notna(row.iloc[sched["lot"]]) else ""
            products = str(row.iloc[sched["products"]]).strip() if pd.notna(row.iloc[sched["products"]]) else ""
            pn_raw = row.iloc[sched["pn"]]
            pn = str(pn_raw).strip() if pd.notna(pn_raw) else ""
            country = str(row.iloc[sched["country"]]).strip() if pd.notna(row.iloc[sched["country"]]) else ""

            ref_base, ref_warning = clean_ref(row.iloc[sched["ref"]])
            if ref_warning:
                result.issues.append(RowIssue(excel_row, lot, "warning", ref_warning))

            standard = _standard_for_country(country, country_standard_map)
            date_fmt = date_format_for(standard)
            exp = compute_exp_date(mfg, shelf_life_months)
            mfg_str = mfg.strftime(date_fmt)
            exp_str = exp.strftime(date_fmt)

            is_japan = country == "일본"
            bsc_hit = bsc_by_pn.get(pn_raw) if pd.notna(pn_raw) else None

            # REF: 일본이면 BSC 리스트 우선, 그 외/미매칭은 스케줄 J열 값
            ref = ref_base
            if is_japan:
                if bsc_hit is not None and pd.notna(bsc_hit[0]):
                    ref = str(bsc_hit[0]).strip()
                else:
                    result.issues.append(RowIssue(
                        excel_row, lot, "warning",
                        f"일본 행의 REF를 BSC 리스트에서 찾지 못해 스케줄 값({ref_base!r})을 사용합니다."))

            # GTIN: 일본은 BSC, 그 외는 품목리스트. 일본 미매칭 시 품목리스트 폴백.
            gtin_raw: object = None
            if is_japan:
                if bsc_hit is not None and pd.notna(bsc_hit[1]):
                    gtin_raw = bsc_hit[1]
                elif pd.notna(pn_raw) and pn_raw in product_gtin_by_pn:
                    gtin_raw = product_gtin_by_pn[pn_raw]
                    result.issues.append(RowIssue(
                        excel_row, lot, "warning",
                        "일본 행의 GTIN을 BSC 리스트에서 찾지 못해 품목리스트 값으로 대체했습니다."))
            else:
                if pd.notna(pn_raw) and pn_raw in product_gtin_by_pn:
                    gtin_raw = product_gtin_by_pn[pn_raw]

            try:
                gtin = normalize_gtin14(gtin_raw)
            except SchemaError as exc:
                gtin = str(gtin_raw).strip()
                result.issues.append(RowIssue(excel_row, lot, "warning",
                                              f"GTIN 정규화 실패: {exc}"))
            if not gtin:
                result.issues.append(RowIssue(
                    excel_row, lot, "warning",
                    "GTIN을 찾지 못했습니다. 입력 파일(품목/BSC 리스트)을 확인하세요."))

            grouped[mfg].append(LabelRecord(
                lot=lot, products=products, pn=pn, ref=ref,
                mfg_date=mfg_str, exp_date=exp_str, gtin=gtin, standard=standard))
        except Exception as exc:  # 행 단위 실패는 기록하고 계속 — 무단 소멸 금지
            result.issues.append(RowIssue(excel_row, "", "error",
                                          f"행 처리 실패: {exc}"))

    for d in sorted(grouped):
        result.records.extend(grouped[d])
    return result
