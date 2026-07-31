"""PDF 문서 래퍼 — PyMuPDF 기반, 유한 페이지 캐시.

레거시 pdf_handler.py의 결함 대응:
- 강제 8배(≈576DPI) 렌더링 → 설정값 줌(기본 4.0)
- pages_cache 무제한 보관 → LRU 상한 (메모리 폭주 해결)
- 워커/GUI 스레드 동시 접근 → 내부 락으로 직렬화
- close 미호출 → close() 명시 + 컨텍스트 매니저
"""

from __future__ import annotations

import os
import threading
from collections import OrderedDict

import numpy as np


class PdfError(RuntimeError):
    pass


class PdfDocument:
    def __init__(self, render_zoom: float = 4.0, cache_pages: int = 6):
        self.render_zoom = render_zoom
        self.cache_pages = cache_pages
        self._doc = None
        self.path: str | None = None
        self.mtime: float = 0.0
        self.page_count: int = 0
        self._lock = threading.Lock()
        self._cache: OrderedDict[int, np.ndarray] = OrderedDict()

    def open(self, path: str) -> None:
        import fitz

        with self._lock:
            if self._doc is not None:
                self._doc.close()
                self._doc = None
            try:
                doc = fitz.open(path)
            except Exception as exc:
                raise PdfError(f"PDF를 열지 못했습니다: {exc}") from exc
            if doc.page_count == 0:
                doc.close()
                raise PdfError("PDF에 페이지가 없습니다.")
            self._doc = doc
            self.path = path
            self.mtime = os.path.getmtime(path)
            self.page_count = doc.page_count
            self._cache.clear()

    @property
    def is_open(self) -> bool:
        return self._doc is not None

    def render_page(self, index: int) -> np.ndarray:
        """페이지를 RGB ndarray로 렌더링. LRU 캐시 상한 유지."""
        import fitz

        with self._lock:
            if self._doc is None:
                raise PdfError("열린 PDF가 없습니다.")
            if not 0 <= index < self.page_count:
                raise PdfError(f"페이지 범위 초과: {index}")
            if index in self._cache:
                self._cache.move_to_end(index)
                return self._cache[index]
            page = self._doc.load_page(index)
            matrix = fitz.Matrix(self.render_zoom, self.render_zoom)
            pixmap = page.get_pixmap(matrix=matrix, colorspace=fitz.csRGB, alpha=False)
            image = np.frombuffer(pixmap.samples, dtype=np.uint8).reshape(
                pixmap.height, pixmap.width, 3).copy()
            self._cache[index] = image
            while len(self._cache) > self.cache_pages:
                self._cache.popitem(last=False)
            return image

    def close(self) -> None:
        with self._lock:
            if self._doc is not None:
                self._doc.close()
                self._doc = None
            self._cache.clear()
            self.path = None
            self.page_count = 0

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()
