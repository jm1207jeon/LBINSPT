"""페이지 분석 결과 캐시 — OCR 단어 + 바코드 검출을 한 키로 보관.

페이지 이동 시 무지연 표시와 Textract 재과금 방지가 목적.
키는 (문서 경로, mtime, 페이지, 렌더 줌, 전처리 시그니처)의 sha1.
메모리 LRU + 디스크 JSON 영속으로, 앱을 껐다 켜도 같은 PDF 재열람은 무과금.
"""

from __future__ import annotations

import hashlib
import json
from collections import OrderedDict
from dataclasses import dataclass, field
from pathlib import Path

from labelsuite.core.barcode.detector import BarcodeHit
from labelsuite.core.ocr.textract_client import OcrWord


@dataclass
class PageAnalysis:
    words: list[OcrWord] = field(default_factory=list)
    barcodes: list[BarcodeHit] = field(default_factory=list)


def page_cache_key(doc_path: str, mtime: float, page: int,
                   render_zoom: float, preprocess_sig: str) -> str:
    raw = f"{doc_path}|{mtime:.3f}|{page}|{render_zoom:.2f}|{preprocess_sig}"
    return hashlib.sha1(raw.encode("utf-8")).hexdigest()


def frame_cache_key(frame_bytes: bytes, preprocess_sig: str) -> str:
    digest = hashlib.sha1(frame_bytes).hexdigest()
    return hashlib.sha1(f"frame|{digest}|{preprocess_sig}".encode()).hexdigest()


def _serialize(analysis: PageAnalysis) -> dict:
    return {
        "words": [{"text": w.text, "bbox": list(w.bbox), "confidence": w.confidence}
                  for w in analysis.words],
        "barcodes": [{"symbology": b.symbology, "text": b.text,
                      "bbox": list(b.bbox), "is_gs1": b.is_gs1}
                     for b in analysis.barcodes],
    }


def _deserialize(data: dict) -> PageAnalysis:
    return PageAnalysis(
        words=[OcrWord(text=w["text"], bbox=tuple(w["bbox"]),
                       confidence=w["confidence"]) for w in data.get("words", [])],
        barcodes=[BarcodeHit(symbology=b["symbology"], text=b["text"],
                             bbox=tuple(b["bbox"]), is_gs1=b["is_gs1"])
                  for b in data.get("barcodes", [])],
    )


class OcrCache:
    def __init__(self, directory: Path | None, max_entries: int = 500):
        self.directory = directory
        self.max_entries = max_entries
        self._memory: OrderedDict[str, PageAnalysis] = OrderedDict()
        if directory is not None:
            directory.mkdir(parents=True, exist_ok=True)

    def get(self, key: str) -> PageAnalysis | None:
        if key in self._memory:
            self._memory.move_to_end(key)
            return self._memory[key]
        if self.directory is not None:
            path = self.directory / f"{key}.json"
            if path.exists():
                try:
                    with open(path, encoding="utf-8") as f:
                        analysis = _deserialize(json.load(f))
                except (OSError, json.JSONDecodeError, KeyError, TypeError):
                    return None
                self._remember(key, analysis)
                return analysis
        return None

    def put(self, key: str, analysis: PageAnalysis) -> None:
        self._remember(key, analysis)
        if self.directory is not None:
            try:
                with open(self.directory / f"{key}.json", "w", encoding="utf-8") as f:
                    json.dump(_serialize(analysis), f, ensure_ascii=False)
            except OSError:
                pass  # 디스크 캐시 실패는 치명적이지 않음
            self._prune_disk()

    def _remember(self, key: str, analysis: PageAnalysis) -> None:
        self._memory[key] = analysis
        self._memory.move_to_end(key)
        while len(self._memory) > self.max_entries:
            self._memory.popitem(last=False)

    def _prune_disk(self) -> None:
        if self.directory is None:
            return
        files = sorted(self.directory.glob("*.json"), key=lambda p: p.stat().st_mtime)
        for path in files[: max(0, len(files) - self.max_entries)]:
            try:
                path.unlink()
            except OSError:
                pass
