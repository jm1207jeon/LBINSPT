"""검사 엔진 — 위젯과 완전 분리된 순수 모델.

레거시 main_window.py의 검증된 매칭/카운팅/LOT 자동 매칭 로직을 이식하되,
합불 상태를 QLabel 텍스트가 아닌 데이터클래스에 보관한다
(레거시는 라벨 텍스트를 split(':')로 역파싱해 _Passed 판정이 불가능했다).
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from datetime import datetime
from difflib import SequenceMatcher

from labelsuite.core.ocr.textract_client import OcrWord
from labelsuite.core.schema import LabelRecord, normalize_gtin14
from labelsuite.core.standards import StandardsBundle, StandardSpec

# 필드명 자체/라벨 형식은 값 카운트에서 제외 (레거시 규칙)
_FIELD_NAME_WORDS = {"LOT", "PN", "REF", "MFG DATE", "EXP DATE", "PRODUCTS"}
_EXCLUDED_WORDS = ("Lasso", "Stent", "Delivery", "Device", "Use")
_GTIN_AI_RE = re.compile(r"\(01\)(\d{14})(?=\(|\s|$)")

# 목록 파일에 존재할 수 있는 날짜 표기들
_DATE_PARSE_FORMATS = ("%Y-%m-%d", "%Y.%m.%d", "%Y/%m/%d", "%Y%m%d")


@dataclass(frozen=True)
class TextMatch:
    field: str
    word: OcrWord
    matched_term: str


@dataclass
class FieldResult:
    field: str
    term: str                    # 검사에 사용한 값 ('' = 값 없음)
    expected: int | None         # None = 규격에 기준 없음(정보성)
    found: int = 0
    matches: list[TextMatch] = field(default_factory=list)

    @property
    def gating(self) -> bool:
        """합불 판정에 반영되는 필드인가 (기준 존재 + 검사 값 존재)."""
        return self.expected is not None and self.expected > 0 and bool(self.term)

    @property
    def passed(self) -> bool:
        return not self.gating or self.found == self.expected


@dataclass(frozen=True)
class CrossCheckResult:
    source: str                  # 바코드 심볼로지 등
    field: str
    barcode_value: str
    expected_value: str
    matched: bool


@dataclass
class InspectionOutcome:
    record: LabelRecord
    standard: StandardSpec
    fields: dict[str, FieldResult] = field(default_factory=dict)
    barcode_checks: list[CrossCheckResult] = field(default_factory=list)

    @property
    def passed(self) -> bool:
        fields_ok = all(f.passed for f in self.fields.values())
        barcodes_ok = all(c.matched for c in self.barcode_checks)
        return fields_ok and barcodes_ok

    @property
    def all_matches(self) -> list[TextMatch]:
        return [m for f in self.fields.values() for m in f.matches]


@dataclass(frozen=True)
class LotMatchResult:
    lot: str
    candidate: str
    match_type: str              # 'exact' | 'suffix_unique' | 'suffix_best'
    confidence: int
    score: float = 0.0


def _reformat_date(value: str, date_format: str) -> str:
    """목록의 날짜 문자열을 규격 포맷으로 재표기. 파싱 불가면 원본 유지."""
    text = (value or "").strip()
    for fmt in _DATE_PARSE_FORMATS:
        try:
            return datetime.strptime(text, fmt).strftime(date_format)
        except ValueError:
            continue
    return text


class InspectionEngine:
    def __init__(self, standards: StandardsBundle):
        self.standards = standards

    # ---------- 검색어 구성 ----------

    def build_search_terms(self, record: LabelRecord,
                           standard: StandardSpec) -> dict[str, str]:
        """필드 → 검사 값. 날짜는 규격 포맷으로 재표기, CHINA는 REF 접두로 해석."""
        terms = {
            "LOT": (record.lot or "").strip(),
            "PRODUCTS": (record.products or "").strip(),
            "PN": (record.pn or "").strip(),
            "REF": (record.ref or "").strip(),
            "MFG DATE": _reformat_date(record.mfg_date, standard.date_format),
            "EXP DATE": _reformat_date(record.exp_date, standard.date_format),
            "GTIN": normalize_gtin14(record.gtin) if record.gtin else "",
        }
        if standard.uses_china_field:
            terms["CHINA"] = self.standards.china_code_for_ref(record.ref) or ""
        return terms

    # ---------- 카운팅 ----------

    @staticmethod
    def _text_counts(term: str, word: OcrWord) -> bool:
        """텍스트 필드 카운트 규칙 (레거시 update_inspection_counts 이식)."""
        text = word.text.strip()
        return (
            "(01)" not in text
            and term.lower() in text.lower()
            and text.upper() not in _FIELD_NAME_WORDS
            and not text.upper().endswith(":")
            and len(text) > 2
            and not any(word_.lower() in text.lower() for word_ in _EXCLUDED_WORDS)
        )

    @staticmethod
    def _gtin_counts(gtin14: str, word: OcrWord) -> bool:
        """GTIN은 (01)+14자리 패턴이 정확히 일치할 때만 카운트."""
        match = _GTIN_AI_RE.search(word.text.strip())
        return bool(match and match.group(1) == gtin14)

    def count_field(self, field_name: str, term: str,
                    ocr_words: list[OcrWord]) -> list[TextMatch]:
        if not term:
            return []
        matcher = self._gtin_counts if field_name == "GTIN" else self._text_counts
        return [TextMatch(field_name, w, term)
                for w in ocr_words if matcher(term, w)]

    # ---------- 검사 ----------

    def inspect(self, record: LabelRecord, standard_name: str,
                ocr_words: list[OcrWord],
                barcode_checks: list[CrossCheckResult] = (),
                extra_search: str = "") -> InspectionOutcome:
        standard = self.standards.spec(standard_name)
        terms = self.build_search_terms(record, standard)
        outcome = InspectionOutcome(record=record, standard=standard,
                                    barcode_checks=list(barcode_checks))
        for field_name, term in terms.items():
            expected = standard.counts.get(field_name)
            matches = self.count_field(field_name, term, ocr_words)
            outcome.fields[field_name] = FieldResult(
                field=field_name, term=term, expected=expected,
                found=len(matches), matches=matches)
        if extra_search.strip():
            matches = self.count_field("SEARCH", extra_search.strip(), ocr_words)
            outcome.fields["SEARCH"] = FieldResult(
                field="SEARCH", term=extra_search.strip(), expected=None,
                found=len(matches), matches=matches)
        return outcome

    # ---------- LOT 자동 매칭 (레거시 2370–2560 이식) ----------

    _LOT_PATTERNS = (
        re.compile(r"^\d{8}$"),
        re.compile(r"^\d{2}[A-Z]\d{3}$"),
        re.compile(r"^[A-Z]{2}\d{4}$"),
        re.compile(r"^[A-Z0-9]{5,10}$"),
    )

    def extract_lot_candidates(self, ocr_words: list[OcrWord]) -> list[OcrWord]:
        candidates = []
        for word in ocr_words:
            text = word.text.strip()
            if len(text) < 4 or word.confidence < 30:
                continue
            if any(p.match(text) for p in self._LOT_PATTERNS):
                candidates.append(word)
        return candidates

    def match_lot(self, ocr_words: list[OcrWord],
                  records: list[LabelRecord]) -> LotMatchResult | None:
        available = [r.lot for r in records if r.lot]
        if not available:
            return None
        candidates = self.extract_lot_candidates(ocr_words)

        # 1단계: 정확 일치
        for cand in candidates:
            if cand.text.strip() in available:
                return LotMatchResult(lot=cand.text.strip(), candidate=cand.text.strip(),
                                      match_type="exact", confidence=cand.confidence)

        # 2단계: 숫자 LOT의 끝 4자리 유일 일치 / 3단계: 유사도 점수
        best: LotMatchResult | None = None
        for cand in candidates:
            text = cand.text.strip()
            if not (len(text) >= 4 and text.isdigit()):
                continue
            suffix = text[-4:]
            suffix_matches = [lot for lot in available
                              if len(lot) >= 4 and lot[-4:] == suffix]
            if len(suffix_matches) == 1:
                return LotMatchResult(lot=suffix_matches[0], candidate=text,
                                      match_type="suffix_unique",
                                      confidence=cand.confidence)
            for lot in suffix_matches:
                score = self._similarity_score(text, lot, cand.confidence)
                if best is None or score > best.score:
                    best = LotMatchResult(lot=lot, candidate=text,
                                          match_type="suffix_best",
                                          confidence=cand.confidence, score=score)
        return best

    @staticmethod
    def _similarity_score(candidate: str, target: str, ocr_confidence: int) -> float:
        """가중 유사도: 문자열 40% + 접두 30% + OCR 신뢰도 20% + 길이 10%."""
        string_similarity = SequenceMatcher(None, candidate, target).ratio()
        min_len = min(len(candidate), len(target))
        matching_prefix = 0
        for a, b in zip(candidate, target):
            if a != b:
                break
            matching_prefix += 1
        prefix_score = matching_prefix / min_len if min_len else 0.0
        confidence_score = ocr_confidence / 100
        max_len = max(len(candidate), len(target))
        length_score = 1 - abs(len(candidate) - len(target)) / max_len if max_len else 0.0
        return (string_similarity * 0.4 + prefix_score * 0.3
                + confidence_score * 0.2 + length_score * 0.1) * 100
