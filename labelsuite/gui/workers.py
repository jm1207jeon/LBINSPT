"""QThread 워커 — 엑셀 로드/목록 생성/OCR 프리페치를 메인 스레드 밖에서 수행.

레거시 결함 대응:
- LiGen: 스레딩 전무 → 대용량 엑셀에서 UI 프리즈
- Inspector: OCR 진행 중 요청 무시 → 이전 페이지 결과가 새 페이지에 잔류
  → OcrPrefetchWorker는 우선순위 큐 + 요청 세대(generation) 태깅으로 해결
"""

from __future__ import annotations

import heapq
import itertools
import threading
import traceback
from dataclasses import dataclass, field
from datetime import date
from typing import Callable

import numpy as np
from PySide6.QtCore import QThread, Signal

from labelsuite.core.list_generator import (
    ColumnMaps,
    GenerationResult,
    generate_list,
    load_input_frame,
)


class ExcelLoadWorker(QThread):
    """단일 입력 엑셀 로드. finished_ok(key, DataFrame) / failed(key, 메시지)."""

    finished_ok = Signal(str, object)
    failed = Signal(str, str)

    def __init__(self, key: str, path: str, colmaps: ColumnMaps, parent=None):
        super().__init__(parent)
        self._key = key
        self._path = path
        self._colmaps = colmaps

    def run(self) -> None:
        try:
            spec = getattr(self._colmaps, self._key)
            frame = load_input_frame(self._path, spec)
            self.finished_ok.emit(self._key, frame)
        except Exception as exc:
            self.failed.emit(
                self._key,
                f"파일을 읽지 못했습니다: {exc}\n"
                f"(시트 '{getattr(self._colmaps, self._key).sheet}'가 있는지 확인하세요)")


class GenerateWorker(QThread):
    """목록 생성. finished_ok(GenerationResult) / failed(메시지)."""

    finished_ok = Signal(object)
    failed = Signal(str)

    def __init__(self, schedule_df, product_df, bsc_df, selected_dates: set[date],
                 colmaps: ColumnMaps, country_standard_map: dict[str, str],
                 shelf_life_months: int, parent=None):
        super().__init__(parent)
        self._args = (schedule_df, product_df, bsc_df, selected_dates,
                      colmaps, country_standard_map, shelf_life_months)

    def run(self) -> None:
        try:
            result: GenerationResult = generate_list(*self._args)
            self.finished_ok.emit(result)
        except Exception as exc:
            traceback.print_exc()
            self.failed.emit(f"리스트 생성 중 오류: {exc}")


@dataclass(order=True)
class _OcrJob:
    priority: int
    sequence: int
    page: int = field(compare=False)
    generation: int = field(compare=False)
    cache_key: str = field(compare=False)
    render: Callable[[], np.ndarray] | None = field(compare=False, default=None)


class OcrPrefetchWorker(QThread):
    """OCR 우선순위 큐 워커 — 현재 페이지 최우선, 나머지는 백그라운드 프리페치.

    - submit(priority=0)은 사용자가 보고 있는 페이지, priority=1은 프리페치.
    - 같은 (generation, page) 재제출은 무시(in-flight dedup) → Textract 중복 과금 차단.
    - new_generation()으로 문서 교체 시 대기 중인 이전 문서 잡을 전부 무효화.
    - 결과는 (generation, page, cache_key, words)로 방출 — 표시 여부는 수신 측이
      현재 페이지/세대와 대조해 결정하고, 캐시 저장은 항상 수행한다.
    """

    page_done = Signal(int, int, str, object)  # generation, page, cache_key, PageAnalysis
    page_failed = Signal(int, int, str)        # generation, page, 사용자용 메시지
    queue_idle = Signal()

    def __init__(self, ocr_fn: Callable[[np.ndarray], object], parent=None):
        super().__init__(parent)
        self._ocr_fn = ocr_fn
        self._heap: list[_OcrJob] = []
        self._pending: set[tuple[int, int]] = set()   # (generation, page)
        self._lock = threading.Lock()
        self._wakeup = threading.Event()
        self._stop = False
        self._generation = 0
        self._sequence = itertools.count()

    def new_generation(self) -> int:
        """문서(PDF/카메라 세션) 교체 — 대기 중 잡 폐기, 새 세대 번호 반환."""
        with self._lock:
            self._generation += 1
            self._heap.clear()
            self._pending.clear()
            return self._generation

    @property
    def generation(self) -> int:
        return self._generation

    def submit(self, page: int, cache_key: str,
               render: Callable[[], np.ndarray], priority: int = 1) -> None:
        with self._lock:
            key = (self._generation, page)
            if key in self._pending:
                return
            self._pending.add(key)
            heapq.heappush(self._heap, _OcrJob(
                priority=priority, sequence=next(self._sequence), page=page,
                generation=self._generation, cache_key=cache_key, render=render))
        self._wakeup.set()

    def prioritize(self, page: int) -> None:
        """이미 큐에 있는 페이지를 최우선으로 끌어올린다."""
        with self._lock:
            for job in self._heap:
                if job.generation == self._generation and job.page == page:
                    job.priority = 0
            heapq.heapify(self._heap)
        self._wakeup.set()

    def stop(self) -> None:
        self._stop = True
        self._wakeup.set()

    def run(self) -> None:
        while not self._stop:
            with self._lock:
                job = heapq.heappop(self._heap) if self._heap else None
            if job is None:
                self.queue_idle.emit()
                self._wakeup.wait()
                self._wakeup.clear()
                continue
            if job.generation != self._generation:
                continue  # 문서가 바뀐 뒤 남은 잡 — 렌더/과금 없이 폐기
            try:
                image = job.render()
                analysis = self._ocr_fn(image)
            except Exception as exc:
                with self._lock:
                    self._pending.discard((job.generation, job.page))
                self.page_failed.emit(job.generation, job.page, str(exc))
                continue
            with self._lock:
                self._pending.discard((job.generation, job.page))
            self.page_done.emit(job.generation, job.page, job.cache_key, analysis)
