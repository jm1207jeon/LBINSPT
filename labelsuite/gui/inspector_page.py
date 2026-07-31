"""라벨 검사 탭 — PDF/카메라 이미지의 OCR·바코드 검사.

핵심 동작:
- PDF 로드 시 전 페이지를 우선순위 큐로 백그라운드 선행 OCR(프리페치)하고
  결과를 캐싱해, 페이지 이동 시 딜레이 없이 오버레이·카운트를 표시한다.
- 사용자가 보고 있는 페이지는 항상 큐 최우선. 문서 교체 시 이전 잡은 전부 폐기.
- 규격은 목록의 STANDARD 값으로 자동 선택(수동 변경 가능), LOT은 OCR로 자동 매칭.
- 합불은 InspectionOutcome.passed 단일 원천. 결과 저장은 core/annotate 경유.
"""

from __future__ import annotations

import os
from collections import OrderedDict
from functools import partial

import numpy as np
from PySide6.QtCore import Qt, QThread, Signal
from PySide6.QtWidgets import (
    QCheckBox,
    QComboBox,
    QFileDialog,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QMessageBox,
    QPushButton,
    QSlider,
    QSplitter,
    QVBoxLayout,
    QWidget,
)

from labelsuite.core.annotate import (
    make_result_filename,
    render_overlays,
    save_annotated_jpeg,
)
from labelsuite.core.barcode.detector import cross_check_hits, detect_barcodes
from labelsuite.core.config import AppConfig, data_dir
from labelsuite.core.inspection import InspectionEngine, InspectionOutcome
from labelsuite.core.ocr.cache import (
    OcrCache,
    PageAnalysis,
    frame_cache_key,
    page_cache_key,
)
from labelsuite.core.ocr.preprocess import PREPROCESS_SIGNATURE, auto_skew_correction
from labelsuite.core.ocr.textract_client import TextractClient
from labelsuite.core.pdf_document import PdfDocument, PdfError
from labelsuite.core.schema import LabelRecord, load_inspection_list
from labelsuite.core.standards import StandardsBundle
from labelsuite.gui.camera import CameraWorker
from labelsuite.gui.widgets.result_panel import ResultPanel
from labelsuite.gui.widgets.zoomable_view import ZoomableImageView
from labelsuite.gui.workers import OcrPrefetchWorker


class _AwsCheckWorker(QThread):
    checked = Signal(bool, str)

    def __init__(self, client: TextractClient, parent=None):
        super().__init__(parent)
        self._client = client

    def run(self) -> None:
        status = self._client.validate_credentials()
        if status.ok:
            self.checked.emit(True, "AWS: 인증 확인됨")
        else:
            self.checked.emit(False, f"AWS 인증 실패: {status.error}")


class InspectorPage(QWidget):
    status_message = Signal(str)
    aws_status_changed = Signal(bool, str)
    page_inspected = Signal(int, object)   # page, InspectionOutcome — 자동저장/이력 훅

    def __init__(self, config: AppConfig, standards: StandardsBundle,
                 history_db=None, parent=None):
        super().__init__(parent)
        self.config = config
        self.standards = standards
        self.history_db = history_db
        self.engine = InspectionEngine(standards)
        self.textract = TextractClient(
            region=config.settings["aws"].get("region", "ap-northeast-2"),
            profile=config.settings["aws"].get("profile") or None)
        self.ocr_cache = OcrCache(
            data_dir() / "ocr_cache",
            int(config.settings.get("ocr_cache_max_entries", 500)))
        self.pdf = PdfDocument(
            render_zoom=float(config.settings.get("pdf_render_zoom", 4.0)),
            cache_pages=int(config.settings.get("page_image_cache_pages", 6)))

        self.records: list[LabelRecord] = []
        self.current_page = 0
        self.mode: str | None = None            # 'pdf' | 'camera'
        self._analyses: dict[int, PageAnalysis] = {}   # 현재 세대의 페이지별 결과
        self._corrected: OrderedDict[int, np.ndarray] = OrderedDict()  # 스큐 보정 이미지
        self._outcomes: dict[int, InspectionOutcome] = {}
        self._lot_auto_selected = False
        self._camera_worker: CameraWorker | None = None
        self._live_frame: np.ndarray | None = None
        self._frozen_frame: np.ndarray | None = None
        self._aws_ok: bool | None = None

        self.ocr_worker = OcrPrefetchWorker(self._analyze_image, self)
        self.ocr_worker.page_done.connect(self._on_page_done)
        self.ocr_worker.page_failed.connect(self._on_page_failed)
        self.ocr_worker.start()

        self._build_ui()
        self._check_aws()

    # ------------------------------------------------------------------ UI

    def _build_ui(self) -> None:
        splitter = QSplitter(Qt.Orientation.Horizontal, self)

        # ---- 좌측: 목록/규격/결과 ----
        left = QWidget()
        left.setMaximumWidth(460)
        left_layout = QVBoxLayout(left)

        list_group = QGroupBox("검사 목록")
        list_layout = QVBoxLayout(list_group)
        row = QHBoxLayout()
        open_list_button = QPushButton("목록 열기")
        open_list_button.clicked.connect(self._open_list)
        self.list_status = QLabel("목록 없음")
        self.list_status.setStyleSheet("color: gray;")
        row.addWidget(open_list_button)
        row.addWidget(self.list_status, stretch=1)
        list_layout.addLayout(row)

        lot_row = QHBoxLayout()
        lot_row.addWidget(QLabel("LOT:"))
        self.lot_combo = QComboBox()
        self.lot_combo.currentIndexChanged.connect(self._on_lot_changed)
        lot_row.addWidget(self.lot_combo, stretch=1)
        self.lot_match_label = QLabel("")
        lot_row.addWidget(self.lot_match_label)
        list_layout.addLayout(lot_row)

        standard_row = QHBoxLayout()
        standard_row.addWidget(QLabel("검사 규격:"))
        self.standard_combo = QComboBox()
        self.standard_combo.addItems(list(self.standards.standards))
        self.standard_combo.currentIndexChanged.connect(
            lambda _=0: self._reinspect_current())
        standard_row.addWidget(self.standard_combo, stretch=1)
        list_layout.addLayout(standard_row)

        search_row = QHBoxLayout()
        search_row.addWidget(QLabel("추가 검색:"))
        self.search_input = QLineEdit()
        self.search_input.setPlaceholderText("라벨에서 찾을 텍스트 (선택)")
        self.search_input.returnPressed.connect(self._reinspect_current)
        search_row.addWidget(self.search_input, stretch=1)
        list_layout.addLayout(search_row)
        left_layout.addWidget(list_group)

        self.result_panel = ResultPanel(self.standards.field_colors)
        left_layout.addWidget(self.result_panel, stretch=1)

        # ---- 우측: 뷰어/조작 ----
        right = QWidget()
        right_layout = QVBoxLayout(right)

        toolbar = QHBoxLayout()
        open_pdf_button = QPushButton("PDF 열기")
        open_pdf_button.clicked.connect(self._open_pdf)
        self.camera_button = QPushButton("카메라 시작")
        self.camera_button.setCheckable(True)
        self.camera_button.toggled.connect(self._toggle_camera)
        self.capture_button = QPushButton("촬영+검사")
        self.capture_button.setEnabled(False)
        self.capture_button.clicked.connect(self._capture_and_inspect)
        self.resume_button = QPushButton("다시 촬영")
        self.resume_button.setEnabled(False)
        self.resume_button.clicked.connect(self._resume_camera)
        toolbar.addWidget(open_pdf_button)
        toolbar.addWidget(self.camera_button)
        toolbar.addWidget(self.capture_button)
        toolbar.addWidget(self.resume_button)
        toolbar.addStretch(1)
        self.auto_save_check = QCheckBox("자동 저장")
        self.auto_save_check.setChecked(
            bool(self.config.settings.get("auto_save_default", False)))
        toolbar.addWidget(self.auto_save_check)
        save_button = QPushButton("결과 저장")
        save_button.clicked.connect(self._save_current)
        toolbar.addWidget(save_button)
        save_dir_button = QPushButton("저장 경로…")
        save_dir_button.clicked.connect(self._pick_save_dir)
        toolbar.addWidget(save_dir_button)
        right_layout.addLayout(toolbar)

        self.viewer = ZoomableImageView()
        right_layout.addWidget(self.viewer, stretch=1)

        nav = QHBoxLayout()
        self.nav_buttons = []
        for text, delta in (("≪ 첫", "first"), ("< 이전", -1),
                            ("다음 >", 1), ("끝 ≫", "last")):
            button = QPushButton(text)
            button.clicked.connect(partial(self._navigate, delta))
            button.setEnabled(False)
            self.nav_buttons.append(button)
            nav.addWidget(button)
        self.page_label = QLabel("- / -")
        nav.addWidget(self.page_label)
        nav.addStretch(1)
        self.prefetch_label = QLabel("")
        nav.addWidget(self.prefetch_label)
        nav.addWidget(QLabel("배율:"))
        self.zoom_slider = QSlider(Qt.Orientation.Horizontal)
        self.zoom_slider.setRange(10, 500)
        self.zoom_slider.setValue(100)
        self.zoom_slider.setMaximumWidth(160)
        self.zoom_slider.valueChanged.connect(self.viewer.set_zoom_percent)
        self.viewer.zoom_changed.connect(self._sync_zoom_slider)
        nav.addWidget(self.zoom_slider)
        right_layout.addLayout(nav)

        splitter.addWidget(left)
        splitter.addWidget(right)
        splitter.setStretchFactor(0, 0)
        splitter.setStretchFactor(1, 1)
        layout = QHBoxLayout(self)
        layout.addWidget(splitter)

    def _sync_zoom_slider(self, percent: int) -> None:
        self.zoom_slider.blockSignals(True)
        self.zoom_slider.setValue(percent)
        self.zoom_slider.blockSignals(False)

    # ------------------------------------------------------------------ AWS

    def _check_aws(self) -> None:
        self._aws_worker = _AwsCheckWorker(self.textract, self)
        self._aws_worker.checked.connect(self._on_aws_checked)
        self._aws_worker.start()

    def _on_aws_checked(self, ok: bool, text: str) -> None:
        self._aws_ok = ok
        self.aws_status_changed.emit(ok, text)
        if not ok:
            self.status_message.emit(text + " — OCR 실행 전에 설정에서 자격증명을 확인하세요.")

    def apply_config(self) -> None:
        """설정 변경 반영 (규격 테이블/AWS/렌더 줌)."""
        from labelsuite.core.standards import load_standards

        self.standards = load_standards(self.config)
        self.engine = InspectionEngine(self.standards)
        self.result_panel.set_field_colors(self.standards.field_colors)
        current = self.standard_combo.currentText()
        self.standard_combo.blockSignals(True)
        self.standard_combo.clear()
        self.standard_combo.addItems(list(self.standards.standards))
        if current in self.standards.standards:
            self.standard_combo.setCurrentText(current)
        self.standard_combo.blockSignals(False)
        self.textract = TextractClient(
            region=self.config.settings["aws"].get("region", "ap-northeast-2"),
            profile=self.config.settings["aws"].get("profile") or None)
        self.pdf.render_zoom = float(self.config.settings.get("pdf_render_zoom", 4.0))
        self._check_aws()

    # ------------------------------------------------------------------ 목록

    def load_records(self, records: list[LabelRecord]) -> None:
        """목록 생성 탭 핸드오프 또는 파일 열기로 레코드 수신."""
        self.records = list(records)
        self._lot_auto_selected = False
        self.lot_combo.blockSignals(True)
        self.lot_combo.clear()
        self.lot_combo.addItem("LOT 선택…")
        for record in self.records:
            self.lot_combo.addItem(record.lot)
        self.lot_combo.blockSignals(False)
        self.list_status.setText(f"{len(records)}건 로드됨")
        self.list_status.setStyleSheet("color: black;")
        self._reinspect_current()

    def _open_list(self) -> None:
        start = os.path.dirname(self.config.settings.get("last_list_path", "") or "")
        path, _ = QFileDialog.getOpenFileName(
            self, "검사 목록 열기", start, "Excel 파일 (*.xlsx *.xls)")
        if not path:
            return
        try:
            records, warnings = load_inspection_list(path)
        except Exception as exc:
            QMessageBox.critical(self, "목록 오류", str(exc))
            return
        self.config.settings["last_list_path"] = path
        self.config.save_settings()
        self.load_records(records)
        if warnings:
            QMessageBox.warning(self, "목록 경고", "\n".join(warnings[:20]))

    def current_record(self) -> LabelRecord | None:
        index = self.lot_combo.currentIndex() - 1
        if 0 <= index < len(self.records):
            return self.records[index]
        return None

    def _on_lot_changed(self, index: int) -> None:
        record = self.current_record()
        if record is not None and record.standard in self.standards.standards:
            self.standard_combo.blockSignals(True)
            self.standard_combo.setCurrentText(record.standard)
            self.standard_combo.blockSignals(False)
        self._reinspect_current()

    # ------------------------------------------------------------------ PDF

    def _open_pdf(self) -> None:
        path, _ = QFileDialog.getOpenFileName(self, "라벨 PDF 열기", "",
                                              "PDF 파일 (*.pdf)")
        if not path:
            return
        if self.camera_button.isChecked():
            self.camera_button.setChecked(False)
        try:
            self.pdf.open(path)
        except PdfError as exc:
            QMessageBox.critical(self, "PDF 오류", str(exc))
            return
        self.mode = "pdf"
        self.current_page = 0
        self._analyses.clear()
        self._outcomes.clear()
        self._corrected.clear()
        self._lot_auto_selected = False
        self.ocr_worker.new_generation()
        for button in self.nav_buttons:
            button.setEnabled(True)
        self.status_message.emit(f"PDF 로드: {os.path.basename(path)} "
                                 f"({self.pdf.page_count}페이지)")
        self._show_page(0, fit=True)
        self._submit_prefetch_jobs()

    def _cache_key_for_page(self, page: int) -> str:
        return page_cache_key(self.pdf.path or "", self.pdf.mtime, page,
                              self.pdf.render_zoom, PREPROCESS_SIGNATURE)

    def _corrected_image(self, page: int) -> np.ndarray:
        """렌더링 + 스큐 보정 이미지 (표시·OCR 공용, LRU 보관)."""
        if page in self._corrected:
            self._corrected.move_to_end(page)
            return self._corrected[page]
        image = auto_skew_correction(self.pdf.render_page(page))
        self._corrected[page] = image
        while len(self._corrected) > self.pdf.cache_pages:
            self._corrected.popitem(last=False)
        return image

    def _submit_prefetch_jobs(self) -> None:
        """현재 페이지 최우선 + 정책 범위의 나머지 페이지 프리페치."""
        policy = self.config.settings.get("prefetch_policy", "all")
        if policy == "all":
            pages = range(self.pdf.page_count)
        else:
            try:
                ahead = max(0, int(policy))
            except (TypeError, ValueError):
                ahead = 0
            pages = range(self.current_page,
                          min(self.pdf.page_count, self.current_page + ahead + 1))
        for page in pages:
            key = self._cache_key_for_page(page)
            cached = self.ocr_cache.get(key)
            if cached is not None:
                self._analyses[page] = cached
                continue
            priority = 0 if page == self.current_page else 1
            self.ocr_worker.submit(page, key,
                                   partial(self._corrected_image, page), priority)
        self._update_prefetch_label()

    def _navigate(self, delta) -> None:
        if self.mode != "pdf" or not self.pdf.is_open:
            return
        if delta == "first":
            target = 0
        elif delta == "last":
            target = self.pdf.page_count - 1
        else:
            target = self.current_page + delta
        target = max(0, min(target, self.pdf.page_count - 1))
        if target != self.current_page:
            self.current_page = target
            self._show_page(target)

    def keyPressEvent(self, event) -> None:
        if self.mode == "pdf" and event.key() in (Qt.Key.Key_Left, Qt.Key.Key_Right):
            self._navigate(-1 if event.key() == Qt.Key.Key_Left else 1)
            event.accept()
            return
        super().keyPressEvent(event)

    def _show_page(self, page: int, fit: bool = False) -> None:
        self.page_label.setText(f"{page + 1} / {self.pdf.page_count}")
        image = self._corrected_image(page)

        analysis = self._analyses.get(page)
        if analysis is None:
            analysis = self.ocr_cache.get(self._cache_key_for_page(page))
            if analysis is not None:
                self._analyses[page] = analysis

        if analysis is not None:
            # 캐시 히트 → 즉시 오버레이·카운트 표시 (무지연)
            self._run_inspection(page, analysis, image, fit=fit)
        else:
            # 미완료 → 원본 표시 + 진행 상태, 해당 페이지를 큐 최우선으로
            self.viewer.set_image(image, fit=fit)
            self.result_panel.clear("OCR 진행 중…")
            key = self._cache_key_for_page(page)
            self.ocr_worker.submit(page, key, partial(self._corrected_image, page),
                                   priority=0)
            self.ocr_worker.prioritize(page)
        self._update_prefetch_label()

    # ------------------------------------------------------------------ OCR 파이프라인

    def _analyze_image(self, image: np.ndarray) -> PageAnalysis:
        """워커 스레드에서 실행: Textract OCR + 로컬 바코드 검출."""
        words = self.textract.detect_words(image)
        barcodes = detect_barcodes(image)
        return PageAnalysis(words=words, barcodes=barcodes)

    def _on_page_done(self, generation: int, page: int, cache_key: str,
                      analysis: PageAnalysis) -> None:
        # 캐시는 세대와 무관하게 저장 (과금된 결과는 버리지 않는다)
        self.ocr_cache.put(cache_key, analysis)
        if generation != self.ocr_worker.generation:
            return
        self._analyses[page] = analysis
        self._update_prefetch_label()
        if self.mode == "pdf" and page == self.current_page:
            self._run_inspection(page, analysis, self._corrected_image(page))
        elif self.mode == "camera" and self._frozen_frame is not None:
            self._run_inspection(page, analysis, self._frozen_frame)

    def _on_page_failed(self, generation: int, page: int, message: str) -> None:
        if generation != self.ocr_worker.generation:
            return
        self.status_message.emit(f"{page + 1}페이지 OCR 실패: {message}")
        if page == self.current_page:
            self.result_panel.clear(f"OCR 실패: {message}")
        QMessageBox.warning(self, "OCR 실패", message)

    def _update_prefetch_label(self) -> None:
        if self.mode != "pdf" or not self.pdf.is_open:
            self.prefetch_label.setText("")
            return
        done = len(self._analyses)
        total = self.pdf.page_count
        if done >= total:
            self.prefetch_label.setText(f"OCR 완료 {done}/{total} 페이지 ✓")
        else:
            self.prefetch_label.setText(f"OCR 완료 {done}/{total} 페이지…")

    # ------------------------------------------------------------------ 검사

    def _run_inspection(self, page: int, analysis: PageAnalysis,
                        image: np.ndarray, fit: bool = False) -> None:
        # LOT 자동 매칭 (최초 1회, 수동 선택 전)
        if (not self._lot_auto_selected and self.records
                and self.lot_combo.currentIndex() <= 0):
            match = self.engine.match_lot(analysis.words, self.records)
            if match is not None:
                self._lot_auto_selected = True
                index = self.lot_combo.findText(match.lot)
                if index > 0:
                    self.lot_combo.blockSignals(True)
                    self.lot_combo.setCurrentIndex(index)
                    self.lot_combo.blockSignals(False)
                    self._on_lot_changed(index)
                self.lot_match_label.setText(
                    {"exact": "자동(정확)", "suffix_unique": "자동(끝4자리)",
                     "suffix_best": "자동(유사)"}.get(match.match_type, "자동"))

        record = self.current_record()
        if record is None:
            self.viewer.set_image(image, fit=fit)
            self.result_panel.clear("LOT을 선택하면 검사를 시작합니다"
                                    if self.records else
                                    "검사 목록을 먼저 로드하세요")
            return

        standard_name = self.standard_combo.currentText()
        barcode_checks = cross_check_hits(analysis.barcodes, record)
        outcome = self.engine.inspect(record, standard_name, analysis.words,
                                      barcode_checks,
                                      extra_search=self.search_input.text())
        self._outcomes[page] = outcome
        self.result_panel.show_outcome(outcome)
        annotated = render_overlays(image, outcome.all_matches,
                                    self.standards.field_colors)
        self.viewer.set_image(annotated, fit=fit)
        self.page_inspected.emit(page, outcome)
        if self.auto_save_check.isChecked():
            self._save_outcome(page, outcome, image, notify=False)

    def _reinspect_current(self) -> None:
        """LOT/규격/검색어 변경 시 캐시된 분석으로 즉시 재검사."""
        if self.mode == "pdf" and self.pdf.is_open:
            analysis = self._analyses.get(self.current_page)
            if analysis is not None:
                self._run_inspection(self.current_page, analysis,
                                     self._corrected_image(self.current_page))
        elif self.mode == "camera" and self._frozen_frame is not None:
            analysis = self._analyses.get(0)
            if analysis is not None:
                self._run_inspection(0, analysis, self._frozen_frame)

    # ------------------------------------------------------------------ 카메라

    def _toggle_camera(self, on: bool) -> None:
        if on:
            self.mode = "camera"
            self.camera_button.setText("카메라 중지")
            self.capture_button.setEnabled(True)
            self.resume_button.setEnabled(False)
            self._frozen_frame = None
            for button in self.nav_buttons:
                button.setEnabled(False)
            index = int(self.config.settings.get("camera_index", 0))
            self._camera_worker = CameraWorker(index, parent=self)
            self._camera_worker.frame_ready.connect(self._on_frame)
            self._camera_worker.failed.connect(self._on_camera_failed)
            self._camera_worker.start()
        else:
            self.camera_button.setText("카메라 시작")
            self.capture_button.setEnabled(False)
            self.resume_button.setEnabled(False)
            if self._camera_worker is not None:
                self._camera_worker.stop()
                self._camera_worker = None
            if self.mode == "camera":
                self.mode = None
                self.viewer.show_message("LIVE CAM 또는 PDF를 선택하세요")

    def _on_frame(self, frame: np.ndarray) -> None:
        self._live_frame = frame
        if self._frozen_frame is None:
            first = self.viewer._base_pixmap is None
            self.viewer.set_image(frame, fit=first)

    def _on_camera_failed(self, message: str) -> None:
        self.camera_button.setChecked(False)
        QMessageBox.warning(self, "카메라 오류", message)

    def _capture_and_inspect(self) -> None:
        if self._live_frame is None:
            return
        self._frozen_frame = self._live_frame.copy()
        self._frozen_frame = auto_skew_correction(self._frozen_frame)
        self.capture_button.setEnabled(False)
        self.resume_button.setEnabled(True)
        self.viewer.set_image(self._frozen_frame)
        self.result_panel.clear("OCR 진행 중…")
        self._analyses.clear()
        self._outcomes.clear()
        self._lot_auto_selected = False
        self.ocr_worker.new_generation()
        key = frame_cache_key(self._frozen_frame.tobytes(), PREPROCESS_SIGNATURE)
        cached = self.ocr_cache.get(key)
        if cached is not None:
            self._analyses[0] = cached
            self._run_inspection(0, cached, self._frozen_frame)
            return
        frozen = self._frozen_frame
        self.ocr_worker.submit(0, key, lambda: frozen, priority=0)

    def _resume_camera(self) -> None:
        self._frozen_frame = None
        self.capture_button.setEnabled(True)
        self.resume_button.setEnabled(False)
        self.result_panel.clear()

    # ------------------------------------------------------------------ 저장

    def _save_dir(self) -> str:
        configured = self.config.settings.get("save_directory") or ""
        return configured or os.path.join(os.path.expanduser("~"), "LabelSuite_결과")

    def _pick_save_dir(self) -> None:
        path = QFileDialog.getExistingDirectory(self, "결과 저장 폴더", self._save_dir())
        if path:
            self.config.settings["save_directory"] = path
            self.config.save_settings()
            self.status_message.emit(f"저장 경로: {path}")

    def _next_counter(self) -> int:
        if self.history_db is not None:
            return self.history_db.next_file_counter()
        counter = int(self.config.settings.get("file_counter", 0)) + 1
        self.config.settings["file_counter"] = counter
        self.config.save_settings()
        return counter

    def _current_source_image(self) -> np.ndarray | None:
        if self.mode == "pdf" and self.pdf.is_open:
            return self._corrected_image(self.current_page)
        if self.mode == "camera" and self._frozen_frame is not None:
            return self._frozen_frame
        return None

    def _save_current(self) -> None:
        page = self.current_page if self.mode == "pdf" else 0
        outcome = self._outcomes.get(page)
        image = self._current_source_image()
        if outcome is None or image is None:
            QMessageBox.information(self, "저장", "저장할 검사 결과가 없습니다.")
            return
        self._save_outcome(page, outcome, image, notify=True)

    def _save_outcome(self, page: int, outcome: InspectionOutcome,
                      image: np.ndarray, notify: bool) -> None:
        filename = make_result_filename(
            self._next_counter(), outcome.record.lot, outcome.record.ref,
            outcome.passed)
        path = os.path.join(self._save_dir(), filename)
        try:
            save_annotated_jpeg(
                image, outcome, self.standards.field_colors, path,
                scale=float(self.config.settings.get("save_scale", 0.5)),
                quality=int(self.config.settings.get("jpeg_quality", 90)))
        except OSError as exc:
            QMessageBox.critical(self, "저장 실패", str(exc))
            return
        self._record_history(outcome, path, page)
        message = f"저장됨: {filename}"
        self.status_message.emit(message)
        if notify:
            QMessageBox.information(self, "저장 완료", message)

    def _record_history(self, outcome: InspectionOutcome, image_path: str,
                        page: int) -> None:
        if self.history_db is None:
            return
        self.history_db.record_inspection(
            outcome, image_path,
            source=self.mode or "unknown",
            pdf_path=self.pdf.path if self.mode == "pdf" else None,
            page=page if self.mode == "pdf" else None)

    # ------------------------------------------------------------------ 종료

    def shutdown(self) -> None:
        self.ocr_worker.stop()
        self.ocr_worker.wait(2000)
        aws_worker = getattr(self, "_aws_worker", None)
        if aws_worker is not None and aws_worker.isRunning():
            aws_worker.wait(5000)
        if self._camera_worker is not None:
            self._camera_worker.stop()
        self.pdf.close()
