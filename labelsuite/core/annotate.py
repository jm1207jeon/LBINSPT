"""하이라이트 렌더링·결과 저장의 단일 구현.

레거시에는 렌더러가 3벌(화면/저장/미사용) 있어 규칙이 서로 달랐고,
저장 경로만 rgbSwapped()를 거쳐 R/B가 반전됐다. 여기서는 입출력 모두
RGB ndarray로 통일하고 저장 시에만 cv2.cvtColor(RGB2BGR)를 적용한다.
"""

from __future__ import annotations

from datetime import date
from pathlib import Path
from typing import Iterable

import cv2
import numpy as np

from labelsuite.core.inspection import InspectionOutcome, TextMatch

_FALLBACK_COLOR = (128, 128, 128, 100)


def render_overlays(image_rgb: np.ndarray, matches: Iterable[TextMatch],
                    colors: dict[str, tuple[int, int, int, int]]) -> np.ndarray:
    """매칭 단어 위에 반투명 색 박스를 그린 RGB 사본을 반환한다."""
    annotated = image_rgb.copy()
    overlay = image_rgb.copy()
    for match in matches:
        x, y, w, h = match.word.bbox
        r, g, b, alpha = colors.get(match.field, _FALLBACK_COLOR)
        cv2.rectangle(overlay, (x, y), (x + w, y + h), (r, g, b), thickness=-1)
        cv2.rectangle(annotated, (x, y), (x + w, y + h), (r, g, b), thickness=2)
    # 반투명 채움 합성 (알파는 필드 공통 상수 0.35 — 레거시 QColor alpha≈100/255)
    cv2.addWeighted(overlay, 0.35, annotated, 0.65, 0, dst=annotated)
    return annotated


def render_summary_box(image_rgb: np.ndarray, outcome: InspectionOutcome) -> np.ndarray:
    """우상단에 필드별 found/expected 요약 박스를 그린다 (저장본용)."""
    annotated = image_rgb.copy()
    lines = [f"[{outcome.standard.name}] " + ("PASSED" if outcome.passed else "CHECK")]
    for field_name, result in outcome.fields.items():
        if result.expected is None:
            continue
        mark = "OK" if result.passed else "NG"
        lines.append(f"{field_name}: {result.found}/{result.expected} {mark}")

    line_height = 28
    box_width = 260
    box_height = line_height * len(lines) + 16
    x0 = max(0, annotated.shape[1] - box_width - 10)
    y0 = 10
    cv2.rectangle(annotated, (x0, y0), (x0 + box_width, y0 + box_height),
                  (255, 255, 255), thickness=-1)
    cv2.rectangle(annotated, (x0, y0), (x0 + box_width, y0 + box_height),
                  (0, 0, 0), thickness=1)
    for i, line in enumerate(lines):
        ok_line = i == 0 and outcome.passed or line.endswith("OK")
        color = (0, 130, 0) if ok_line else (200, 0, 0)
        cv2.putText(annotated, line, (x0 + 8, y0 + line_height * (i + 1)),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.6, color, 2, cv2.LINE_AA)
    return annotated


def save_annotated_jpeg(image_rgb: np.ndarray, outcome: InspectionOutcome,
                        colors: dict[str, tuple[int, int, int, int]],
                        path: str, scale: float = 0.5, quality: int = 90) -> None:
    """오버레이+요약 박스를 넣은 결과 JPEG 저장. RGB→BGR 변환은 여기 한 곳뿐이다."""
    annotated = render_overlays(image_rgb, outcome.all_matches, colors)
    annotated = render_summary_box(annotated, outcome)
    if 0 < scale < 1:
        annotated = cv2.resize(annotated, None, fx=scale, fy=scale,
                               interpolation=cv2.INTER_AREA)
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    ok = cv2.imwrite(path, cv2.cvtColor(annotated, cv2.COLOR_RGB2BGR),
                     [cv2.IMWRITE_JPEG_QUALITY, quality])
    if not ok:
        raise OSError(f"이미지 저장 실패: {path}")


def make_result_filename(counter: int, lot: str, ref: str,
                         passed: bool, when: date | None = None) -> str:
    """###_LOT_REF_YYYYMMDD_Passed|_Check.jpg — 합불은 InspectionOutcome.passed 기준."""
    when = when or date.today()
    safe_lot = "".join(c for c in (lot or "NOLOT") if c.isalnum() or c in "-_") or "NOLOT"
    safe_ref = "".join(c for c in (ref or "NOREF") if c.isalnum() or c in "-_") or "NOREF"
    suffix = "Passed" if passed else "Check"
    return f"{counter:03d}_{safe_lot}_{safe_ref}_{when:%Y%m%d}_{suffix}.jpg"
