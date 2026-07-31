import pytest

from labelsuite.core.config import AppConfig
from labelsuite.core.inspection import CrossCheckResult, InspectionEngine
from labelsuite.core.ocr.textract_client import OcrWord
from labelsuite.core.schema import LabelRecord
from labelsuite.core.standards import load_standards


@pytest.fixture
def engine(tmp_path):
    return InspectionEngine(load_standards(AppConfig(tmp_path / "cfg")))


def word(text, conf=95, bbox=(0, 0, 10, 10)):
    return OcrWord(text=text, bbox=bbox, confidence=conf)


RECORD = LabelRecord(
    lot="25090776", products="HANAROSTENT X", pn="HANARO-01", ref="NCN20-080-230",
    mfg_date="2024-05-10", exp_date="2027-05-09", gtin="08806173612345",
    standard="MDR")


class TestSearchTerms:
    def test_basic_terms(self, engine):
        terms = engine.build_search_terms(RECORD, engine.standards.spec("MDR"))
        assert terms["LOT"] == "25090776"
        assert terms["GTIN"] == "08806173612345"
        assert "CHINA" not in terms

    def test_bsc_reformats_dates(self, engine):
        terms = engine.build_search_terms(RECORD, engine.standards.spec("BSC"))
        assert terms["MFG DATE"] == "2024.05.10"
        assert terms["EXP DATE"] == "2027.05.09"

    def test_china_field_from_ref_prefix(self, engine):
        record = LabelRecord("L", "P", "PN", "BPJ01-001", "2024-05-10",
                             "2027-05-09", "", standard="중국")
        terms = engine.build_search_terms(record, engine.standards.spec("중국"))
        assert terms["CHINA"] == "LBDB-04"

    def test_no_lot_no_crash(self, engine):
        """레거시 NameError(excluded_words 미정의 경로) 회귀 방지."""
        record = LabelRecord("", "", "", "", "", "", "")
        outcome = engine.inspect(record, "MDR", [word("ANYTHING")])
        assert outcome.fields["LOT"].found == 0


class TestCounting:
    def test_counts_substring_matches(self, engine):
        words = [word("25090776"), word("LOT:25090776"), word("no-match")]
        matches = engine.count_field("LOT", "25090776", words)
        assert len(matches) == 2

    def test_excludes_field_names_and_labels(self, engine):
        words = [word("LOT"), word("REF:"), word("ab")]
        assert engine.count_field("LOT", "lot", words) == []

    def test_excludes_gtin_barcode_text_for_text_fields(self, engine):
        words = [word("(01)08806173612345(10)25090776")]
        assert engine.count_field("LOT", "25090776", words) == []

    def test_excluded_words_filter(self, engine):
        words = [word("Stent-25090776")]
        assert engine.count_field("LOT", "25090776", words) == []

    def test_gtin_exact_ai_match_only(self, engine):
        words = [
            word("(01)08806173612345"),
            word("(01)08806173612345(10)25090776"),
            word("08806173612345"),          # AI 없음 → 미카운트
            word("(01)08806173699999"),      # 다른 GTIN
        ]
        matches = engine.count_field("GTIN", "08806173612345", words)
        assert len(matches) == 2


class TestOutcome:
    def _words_for_pass(self, spec):
        words = []
        words += [word(f"x-25090776-{i}") for i in range(spec.counts["LOT"])]
        words += [word(f"HANARO-01-{i}") for i in range(spec.counts["PN"])]
        words += [word(f"NCN20-080-230-{i}") for i in range(spec.counts["REF"])]
        words += [word(f"MFG 2024-05-10 #{i}") for i in range(spec.counts["MFG DATE"])]
        words += [word(f"EXP 2027-05-09 #{i}") for i in range(spec.counts["EXP DATE"])]
        words += [word("(01)08806173612345") for _ in range(spec.counts["GTIN"])]
        return words

    def test_passed_reachable(self, engine):
        """레거시에서는 라벨 텍스트 역파싱 실패로 _Passed 도달 불가였다."""
        spec = engine.standards.spec("MDR")
        outcome = engine.inspect(RECORD, "MDR", self._words_for_pass(spec))
        assert outcome.passed is True

    def test_count_mismatch_fails(self, engine):
        spec = engine.standards.spec("MDR")
        words = self._words_for_pass(spec)[:-1]  # GTIN 1개 부족
        outcome = engine.inspect(RECORD, "MDR", words)
        assert outcome.passed is False
        assert outcome.fields["GTIN"].passed is False

    def test_zero_expected_fields_do_not_gate(self, engine):
        spec = engine.standards.spec("MDR")
        outcome = engine.inspect(RECORD, "MDR", self._words_for_pass(spec))
        assert outcome.fields["PRODUCTS"].expected is None
        assert outcome.fields["PRODUCTS"].passed is True

    def test_barcode_check_gates_outcome(self, engine):
        spec = engine.standards.spec("MDR")
        bad_check = CrossCheckResult("DataMatrix", "GTIN", "0999", "08806173612345", False)
        outcome = engine.inspect(RECORD, "MDR", self._words_for_pass(spec),
                                 barcode_checks=[bad_check])
        assert outcome.passed is False


class TestLotMatch:
    RECORDS = [
        LabelRecord("25090776", "", "", "", "", "", ""),
        LabelRecord("25080776", "", "", "", "", "", ""),
        LabelRecord("24A0001", "", "", "", "", "", ""),
    ]

    def test_exact_match(self, engine):
        result = engine.match_lot([word("25090776")], self.RECORDS)
        assert result.match_type == "exact"
        assert result.lot == "25090776"

    def test_suffix_unique(self, engine):
        records = [LabelRecord("25091234", "", "", "", "", "", ""),
                   LabelRecord("25095678", "", "", "", "", "", "")]
        result = engine.match_lot([word("99991234")], records)
        assert result.match_type == "suffix_unique"
        assert result.lot == "25091234"

    def test_suffix_best_by_similarity(self, engine):
        result = engine.match_lot([word("25090776")[:] if False else word("15090776")],
                                  self.RECORDS)
        assert result is not None
        assert result.match_type == "suffix_best"
        assert result.lot == "25090776"

    def test_low_confidence_ignored(self, engine):
        assert engine.match_lot([word("25090776", conf=10)], self.RECORDS) is None

    def test_no_candidates(self, engine):
        assert engine.match_lot([word("ab")], self.RECORDS) is None


class TestAnnotate:
    def test_render_and_save(self, tmp_path, engine):
        import numpy as np

        from labelsuite.core.annotate import (
            make_result_filename,
            render_overlays,
            save_annotated_jpeg,
        )

        spec = engine.standards.spec("MDR")
        words = TestOutcome()._words_for_pass(spec)
        outcome = engine.inspect(RECORD, "MDR", words)
        image = np.full((200, 300, 3), 255, dtype=np.uint8)
        colors = engine.standards.field_colors

        annotated = render_overlays(image, outcome.all_matches, colors)
        assert annotated.shape == image.shape
        assert not np.array_equal(annotated, image)

        path = tmp_path / "out" / make_result_filename(1, RECORD.lot, RECORD.ref,
                                                       outcome.passed)
        save_annotated_jpeg(image, outcome, colors, str(path))
        assert path.exists()
        assert "_Passed" in path.name

    def test_filename_check_suffix(self):
        from labelsuite.core.annotate import make_result_filename

        name = make_result_filename(7, "L/1", "R:2", False)
        assert name.startswith("007_L1_R2_")
        assert name.endswith("_Check.jpg")
