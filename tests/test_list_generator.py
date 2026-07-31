from datetime import date, datetime

import pandas as pd
import pytest

from labelsuite.core.config import AppConfig
from labelsuite.core.list_generator import (
    ColumnMaps,
    clean_ref,
    compute_exp_date,
    extract_available_dates,
    generate_list,
    load_input_frame,
)
from tests.conftest import (
    build_bsc_xlsx,
    build_product_xlsx,
    build_schedule_xlsx,
    schedule_row,
)

COUNTRY_MAP = {"일본": "BSC", "중국": "중국", "*": "MDR"}


@pytest.fixture
def colmaps(tmp_path):
    return ColumnMaps.from_config(AppConfig(tmp_path / "cfg").column_maps_raw)


def _frames(tmp_path, colmaps, schedule_rows, product_rows=None, bsc_rows=None):
    sched = load_input_frame(
        str(build_schedule_xlsx(tmp_path / "sched.xlsx", schedule_rows)), colmaps.schedule)
    product = None
    if product_rows is not None:
        product = load_input_frame(
            str(build_product_xlsx(tmp_path / "prod.xlsx", product_rows)), colmaps.product)
    bsc = None
    if bsc_rows is not None:
        bsc = load_input_frame(
            str(build_bsc_xlsx(tmp_path / "bsc.xlsx", bsc_rows)), colmaps.bsc)
    return sched, product, bsc


class TestComputeExpDate:
    def test_normal_date(self):
        assert compute_exp_date(date(2024, 5, 10)) == date(2027, 5, 9)

    def test_feb_29_no_crash(self):
        """레거시는 replace(year+3)로 ValueError → 행 소멸. 2024-02-29 → 2027-02-27."""
        assert compute_exp_date(date(2024, 2, 29)) == date(2027, 2, 27)

    def test_custom_shelf_life(self):
        assert compute_exp_date(date(2024, 5, 10), shelf_life_months=24) == date(2026, 5, 9)


class TestCleanRef:
    def test_normal_ref_kept(self):
        assert clean_ref("NCN20-080-230") == ("NCN20-080-230", None)

    def test_ref_with_colon_kept(self):
        """레거시는 ':' 포함 시 무조건 비움 — 정상 REF를 지우는 버그."""
        ref, warning = clean_ref("ABC:12")
        assert ref == "ABC:12"
        assert warning is None

    def test_large_numeric_string_ref_kept(self):
        """레거시는 문자열이라도 float 캐스팅해 40000 초과면 비움."""
        assert clean_ref("50001")[0] == "50001"

    def test_datetime_instance_blanked_with_warning(self):
        ref, warning = clean_ref(pd.Timestamp("2024-05-10"))
        assert ref == ""
        assert warning is not None

    def test_full_date_string_blanked(self):
        ref, warning = clean_ref("2024-05-10 00:00:00")
        assert ref == ""
        assert warning is not None

    def test_numeric_excel_serial_blanked(self):
        ref, warning = clean_ref(45000)
        assert ref == ""
        assert "시리얼" in warning

    def test_nan_silent(self):
        assert clean_ref(float("nan")) == ("", None)


class TestGenerateList:
    def test_non_japan_row(self, tmp_path, colmaps):
        sched, product, bsc = _frames(
            tmp_path, colmaps,
            [schedule_row(mfg=datetime(2024, 5, 10))],
            product_rows=[("HANARO-01", 8806173612345)],
        )
        result = generate_list(sched, product, bsc, {date(2024, 5, 10)},
                               colmaps, COUNTRY_MAP)
        assert len(result.records) == 1
        r = result.records[0]
        assert r.lot == "24A1234"
        assert r.ref == "NCN20-080-230"
        assert r.mfg_date == "2024-05-10"
        assert r.exp_date == "2027-05-09"
        assert r.gtin == "08806173612345"  # GTIN-14 정규화 (레거시는 13자리)
        assert r.standard == "MDR"

    def test_japan_row_uses_bsc_and_dot_dates(self, tmp_path, colmaps):
        sched, product, bsc = _frames(
            tmp_path, colmaps,
            [schedule_row(country="일본", mfg=datetime(2024, 5, 10))],
            product_rows=[("HANARO-01", 8806173612345)],
            bsc_rows=[("M730-BSC-REF", "HANARO-01", "4987654321098")],
        )
        result = generate_list(sched, product, bsc, {date(2024, 5, 10)},
                               colmaps, COUNTRY_MAP)
        r = result.records[0]
        assert r.ref == "M730-BSC-REF"
        assert r.mfg_date == "2024.05.10"
        assert r.exp_date == "2027.05.09"
        assert r.gtin == "04987654321098"
        assert r.standard == "BSC"

    def test_feb29_row_survives(self, tmp_path, colmaps):
        """레거시에서 조용히 사라지던 2/29 제조 행이 생성되는지."""
        sched, product, bsc = _frames(
            tmp_path, colmaps,
            [schedule_row(mfg=datetime(2024, 2, 29))],
            product_rows=[("HANARO-01", 8806173612345)],
        )
        result = generate_list(sched, product, bsc, {date(2024, 2, 29)},
                               colmaps, COUNTRY_MAP)
        assert len(result.records) == 1
        assert result.records[0].exp_date == "2027-02-27"
        assert result.error_count == 0

    def test_japan_bsc_miss_falls_back_to_product(self, tmp_path, colmaps):
        """레거시는 일본 GTIN 미매칭 시 빈 값 — 폴백 + 경고로 개선."""
        sched, product, bsc = _frames(
            tmp_path, colmaps,
            [schedule_row(country="일본")],
            product_rows=[("HANARO-01", 8806173612345)],
            bsc_rows=[("REF-X", "OTHER-PN", "999")],
        )
        result = generate_list(sched, product, bsc, {date(2024, 5, 10)},
                               colmaps, COUNTRY_MAP)
        r = result.records[0]
        assert r.gtin == "08806173612345"
        assert any("품목리스트 값으로 대체" in i.message for i in result.issues)
        assert any("BSC 리스트에서 찾지 못해" in i.message for i in result.issues)

    def test_missing_gtin_warns_not_silent(self, tmp_path, colmaps):
        sched, product, bsc = _frames(
            tmp_path, colmaps,
            [schedule_row(pn="UNKNOWN-PN")],
            product_rows=[("HANARO-01", 8806173612345)],
        )
        result = generate_list(sched, product, bsc, {date(2024, 5, 10)},
                               colmaps, COUNTRY_MAP)
        assert result.records[0].gtin == ""
        assert any("GTIN을 찾지 못했습니다" in i.message for i in result.issues)

    def test_date_filter_and_ordering(self, tmp_path, colmaps):
        sched, product, bsc = _frames(
            tmp_path, colmaps,
            [
                schedule_row(lot="L-C", mfg=datetime(2024, 5, 12)),
                schedule_row(lot="L-A", mfg=datetime(2024, 5, 10)),
                schedule_row(lot="L-SKIP", mfg=datetime(2024, 5, 11)),
            ],
            product_rows=[("HANARO-01", 8806173612345)],
        )
        result = generate_list(sched, product, bsc,
                               {date(2024, 5, 10), date(2024, 5, 12)},
                               colmaps, COUNTRY_MAP)
        assert [r.lot for r in result.records] == ["L-A", "L-C"]

    def test_extract_available_dates(self, tmp_path, colmaps):
        sched, _, _ = _frames(
            tmp_path, colmaps,
            [
                schedule_row(mfg=datetime(2024, 5, 12)),
                schedule_row(mfg=datetime(2024, 5, 10)),
                schedule_row(mfg=datetime(2024, 5, 10)),  # 중복
                {5: "NO-DATE"},  # MFG 없음
            ],
        )
        assert extract_available_dates(sched, colmaps) == [date(2024, 5, 10), date(2024, 5, 12)]

    def test_no_dates_selected_returns_empty(self, tmp_path, colmaps):
        sched, product, bsc = _frames(tmp_path, colmaps, [schedule_row()],
                                      product_rows=[("HANARO-01", 1)])
        result = generate_list(sched, product, bsc, set(), colmaps, COUNTRY_MAP)
        assert result.records == []
