"""바코드/데이터매트릭스 검출 — zxing-cpp 단일 라이브러리.

레거시 barcode_detector.py(pyzbar + QR 디텍터를 DataMatrix로 오표기,
의도적 중복 유지, 잘못된 GS1 파싱)는 이식하지 않는다. zxing-cpp는
Code128/EAN/QR/DataMatrix를 하나의 휠로 지원하고 로컬에서 무과금으로 돈다.
"""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from labelsuite.core.barcode.gs1 import Gs1Message, Gs1ParseError, parse_gs1, parse_gs1_date
from labelsuite.core.inspection import CrossCheckResult
from labelsuite.core.schema import LabelRecord, normalize_gtin14

_DATE_PARSE_FORMATS = ("%Y-%m-%d", "%Y.%m.%d", "%Y/%m/%d", "%Y%m%d")


@dataclass(frozen=True)
class BarcodeHit:
    symbology: str
    text: str
    bbox: tuple[int, int, int, int]
    is_gs1: bool


def detect_barcodes(image_rgb: np.ndarray) -> list[BarcodeHit]:
    import zxingcpp

    hits: list[BarcodeHit] = []
    for result in zxingcpp.read_barcodes(image_rgb):
        if not result.valid:
            continue
        # result.text는 GS를 '<GS>'로 이스케이프하므로 raw bytes를 사용한다
        try:
            text = bytes(result.bytes).decode("utf-8", errors="replace")
        except (TypeError, ValueError):
            text = result.text or ""
        if not text:
            continue
        position = result.position
        xs = [position.top_left.x, position.top_right.x,
              position.bottom_left.x, position.bottom_right.x]
        ys = [position.top_left.y, position.top_right.y,
              position.bottom_left.y, position.bottom_right.y]
        x, y = min(xs), min(ys)
        hits.append(BarcodeHit(
            symbology=str(result.format).replace(" ", ""),
            text=text,
            bbox=(x, y, max(xs) - x, max(ys) - y),
            is_gs1=result.content_type == zxingcpp.ContentType.GS1,
        ))
    return hits


def _parse_record_date(value: str):
    from datetime import datetime

    for fmt in _DATE_PARSE_FORMATS:
        try:
            return datetime.strptime((value or "").strip(), fmt).date()
        except ValueError:
            continue
    return None


def cross_check(message: Gs1Message, record: LabelRecord,
                source: str) -> list[CrossCheckResult]:
    """바코드에서 디코딩한 GS1 값과 목록 레코드를 교차 검증한다.

    (01)↔GTIN, (10)↔LOT, (11)↔MFG DATE, (17)↔EXP DATE.
    바코드에 없는 AI는 검증하지 않는다(부재≠불일치).
    """
    checks: list[CrossCheckResult] = []

    gtin = message.get("01")
    if gtin is not None:
        expected = normalize_gtin14(record.gtin) if record.gtin else ""
        checks.append(CrossCheckResult(source, "GTIN", gtin, expected,
                                       bool(expected) and gtin == expected))

    lot = message.get("10")
    if lot is not None:
        expected = (record.lot or "").strip()
        checks.append(CrossCheckResult(source, "LOT", lot, expected,
                                       bool(expected) and lot == expected))

    for ai, field_name, record_value in (("11", "MFG DATE", record.mfg_date),
                                         ("17", "EXP DATE", record.exp_date)):
        raw = message.get(ai)
        if raw is None:
            continue
        barcode_date = parse_gs1_date(raw)
        expected_date = _parse_record_date(record_value)
        matched = (barcode_date is not None and expected_date is not None
                   and barcode_date == expected_date)
        checks.append(CrossCheckResult(
            source, field_name, raw,
            expected_date.isoformat() if expected_date else (record_value or ""),
            matched))
    return checks


def cross_check_hits(hits: list[BarcodeHit],
                     record: LabelRecord) -> list[CrossCheckResult]:
    """검출된 모든 GS1 바코드를 파싱해 교차 검증 결과를 모은다."""
    checks: list[CrossCheckResult] = []
    for hit in hits:
        looks_gs1 = hit.is_gs1 or hit.text.startswith(("(01)", "01")) or GS_IN(hit.text)
        if not looks_gs1:
            continue
        try:
            message = parse_gs1(hit.text)
        except Gs1ParseError:
            continue
        checks.extend(cross_check(message, record, hit.symbology))
    return checks


def GS_IN(text: str) -> bool:
    return "\x1d" in text
