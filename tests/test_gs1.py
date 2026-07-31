from datetime import date

import pytest

from labelsuite.core.barcode.detector import cross_check
from labelsuite.core.barcode.gs1 import (
    GS,
    Gs1ParseError,
    parse_gs1,
    parse_gs1_date,
)
from labelsuite.core.schema import LabelRecord


class TestParseGs1:
    def test_concatenated_fixed_then_variable(self):
        """(01)+(17)+(10) 연결 — 레거시 파서가 깨지던 기본 케이스."""
        message = parse_gs1("01088061736123451727050910A123BC")
        assert message.get("01") == "08806173612345"
        assert message.get("17") == "270509"
        assert message.get("10") == "A123BC"

    def test_lot_value_containing_ai_like_digits(self):
        """LOT 값 '10ABC1723'의 '17'이 AI로 오인되면 안 된다 (레거시 버그)."""
        message = parse_gs1("0108806173612345" + "10" + "10ABC1723")
        assert message.get("01") == "08806173612345"
        assert message.get("10") == "10ABC1723"
        assert message.get("17") is None

    def test_gs_separated_variable_fields(self):
        message = parse_gs1("10LOT-A" + GS + "21SER123")
        assert message.get("10") == "LOT-A"
        assert message.get("21") == "SER123"

    def test_parenthesized_human_readable(self):
        message = parse_gs1("(01)08806173612345(10)25090776(17)270509")
        assert message.get("01") == "08806173612345"
        assert message.get("10") == "25090776"
        assert message.get("17") == "270509"

    def test_symbology_prefix_stripped(self):
        message = parse_gs1("]d20108806173612345")
        assert message.get("01") == "08806173612345"

    def test_unknown_ai_raises(self):
        with pytest.raises(Gs1ParseError):
            parse_gs1("9912345")

    def test_short_fixed_field_raises(self):
        with pytest.raises(Gs1ParseError):
            parse_gs1("01123")

    def test_empty_raises(self):
        with pytest.raises(Gs1ParseError):
            parse_gs1("")


class TestGs1Date:
    def test_normal(self):
        assert parse_gs1_date("270509") == date(2027, 5, 9)

    def test_day_zero_is_month_end(self):
        assert parse_gs1_date("270200") == date(2027, 2, 28)
        assert parse_gs1_date("280200") == date(2028, 2, 29)

    def test_invalid(self):
        assert parse_gs1_date("271350") is None
        assert parse_gs1_date("27050") is None


RECORD = LabelRecord("25090776", "P", "PN", "REF", "2024-05-10", "2027-05-09",
                     "08806173612345")


class TestCrossCheck:
    def test_all_match(self):
        message = parse_gs1("(01)08806173612345(10)25090776(11)240510(17)270509")
        checks = cross_check(message, RECORD, "DataMatrix")
        assert len(checks) == 4
        assert all(c.matched for c in checks)

    def test_gtin_mismatch(self):
        message = parse_gs1("(01)08806173699999(10)25090776")
        checks = {c.field: c for c in cross_check(message, RECORD, "DataMatrix")}
        assert checks["GTIN"].matched is False
        assert checks["LOT"].matched is True

    def test_bsc_dot_dates_match(self):
        record = LabelRecord("L", "P", "PN", "REF", "2024.05.10", "2027.05.09",
                             "08806173612345")
        message = parse_gs1("(01)08806173612345(17)270509")
        checks = {c.field: c for c in cross_check(message, record, "DataMatrix")}
        assert checks["EXP DATE"].matched is True

    def test_absent_ai_not_checked(self):
        message = parse_gs1("(01)08806173612345")
        checks = cross_check(message, RECORD, "Code128")
        assert [c.field for c in checks] == ["GTIN"]


class TestDetectorRoundTrip:
    def test_detect_real_datamatrix(self):
        """zxing-cpp로 생성한 DataMatrix를 다시 검출·파싱하는 왕복 테스트."""
        import numpy as np
        import zxingcpp

        payload = "0108806173612345\x1d1025090776"
        try:
            barcode = zxingcpp.create_barcode(payload, zxingcpp.BarcodeFormat.DataMatrix)
            image = np.array(barcode.to_image(scale=8), dtype=np.uint8)
        except (AttributeError, TypeError):
            pytest.skip("zxing-cpp 버전이 바코드 생성을 지원하지 않음")
        rgb = np.stack([image] * 3, axis=-1)

        from labelsuite.core.barcode.detector import cross_check_hits, detect_barcodes

        hits = detect_barcodes(rgb)
        assert len(hits) == 1
        assert hits[0].symbology == "DataMatrix"
        checks = {c.field: c for c in cross_check_hits(hits, RECORD)}
        assert checks["GTIN"].matched is True
        assert checks["LOT"].matched is True
