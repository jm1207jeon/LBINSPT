"""검사 탭 통합 테스트 — 오프스크린, Textract는 스텁으로 대체.

실제 PDF를 만들어 프리페치 → 캐시 → 페이지 이동 무지연 표시 →
재과금 방지(호출 횟수)까지 검증한다.
"""

import os
import time
from datetime import date

import numpy as np
import pytest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

pytest.importorskip("PySide6")
fitz = pytest.importorskip("fitz")


@pytest.fixture(scope="module")
def qapp():
    from PySide6.QtWidgets import QApplication

    app = QApplication.instance() or QApplication([])
    yield app


@pytest.fixture
def sample_pdf(tmp_path):
    """3페이지 라벨 PDF: 각 페이지에 LOT/REF 텍스트."""
    path = tmp_path / "labels.pdf"
    doc = fitz.open()
    for i in range(3):
        page = doc.new_page(width=400, height = 300)
        page.insert_text((50, 100), f"LOT 2509077{i}")
        page.insert_text((50, 130), "REF NCN20-080-230")
    doc.save(str(path))
    doc.close()
    return str(path)


class StubTextract:
    """호출 횟수를 세는 가짜 Textract — 페이지 텍스트를 그대로 돌려준다."""

    def __init__(self):
        self.calls = 0

    def detect_words(self, image_rgb):
        from labelsuite.core.ocr.textract_client import OcrWord

        self.calls += 1
        return [OcrWord("25090776", (10, 10, 50, 12), 95),
                OcrWord("NCN20-080-230", (10, 40, 80, 12), 95)]


@pytest.fixture
def page(qapp, tmp_path, monkeypatch):
    from labelsuite.core.config import AppConfig
    from labelsuite.core.standards import load_standards
    from labelsuite.gui.inspector_page import InspectorPage, _AwsCheckWorker

    # AWS 체크 스레드가 실제 네트워크를 타지 않도록 무력화
    monkeypatch.setattr(_AwsCheckWorker, "run",
                        lambda self: self.checked.emit(True, "AWS: 스텁"))
    config = AppConfig(tmp_path / "cfg")
    config.settings["save_directory"] = str(tmp_path / "out")
    page = InspectorPage(config, load_standards(config))
    stub = StubTextract()
    page.textract = stub
    page._stub = stub
    yield page
    page.shutdown()


def _wait_until(qapp, predicate, timeout=10.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        qapp.processEvents()
        if predicate():
            return True
        time.sleep(0.02)
    return False


RECORDS_ARGS = ("25090776", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
                "2024-05-10", "2027-05-09", "08806173612345")


def _records():
    from labelsuite.core.schema import LabelRecord

    return [LabelRecord(*RECORDS_ARGS, standard="MDR")]


class TestPrefetchFlow:
    def test_all_pages_prefetched_and_cached(self, qapp, page, sample_pdf):
        page.load_records(_records())
        page.pdf.open(sample_pdf)
        page.mode = "pdf"
        page.current_page = 0
        page.ocr_worker.new_generation()
        page._show_page(0, fit=True)
        page._submit_prefetch_jobs()

        assert _wait_until(qapp, lambda: len(page._analyses) == 3), \
            f"프리페치 미완료: {len(page._analyses)}/3"
        assert page._stub.calls == 3  # 페이지당 정확히 1회 호출

    def test_page_navigation_uses_cache_no_rebilling(self, qapp, page, sample_pdf):
        page.load_records(_records())
        page.pdf.open(sample_pdf)
        page.mode = "pdf"
        page.current_page = 0
        page.ocr_worker.new_generation()
        page._show_page(0, fit=True)
        page._submit_prefetch_jobs()
        assert _wait_until(qapp, lambda: len(page._analyses) == 3)
        calls_after_prefetch = page._stub.calls

        # 페이지 왕복 이동 — 추가 Textract 호출이 없어야 한다
        page._navigate(1)
        page._navigate(1)
        page._navigate("first")
        qapp.processEvents()
        assert page._stub.calls == calls_after_prefetch
        assert page.prefetch_label.text().endswith("✓")

    def test_lot_auto_selected_and_standard_applied(self, qapp, page, sample_pdf):
        page.load_records(_records())
        page.pdf.open(sample_pdf)
        page.mode = "pdf"
        page.current_page = 0
        page.ocr_worker.new_generation()
        page._show_page(0, fit=True)
        page._submit_prefetch_jobs()
        assert _wait_until(qapp, lambda: page.current_page in page._outcomes)
        assert page.lot_combo.currentText() == "25090776"
        assert page.standard_combo.currentText() == "MDR"

    def test_disk_cache_survives_reopen(self, qapp, page, sample_pdf):
        page.load_records(_records())
        page.pdf.open(sample_pdf)
        page.mode = "pdf"
        page.current_page = 0
        page.ocr_worker.new_generation()
        page._show_page(0, fit=True)
        page._submit_prefetch_jobs()
        assert _wait_until(qapp, lambda: len(page._analyses) == 3)
        calls_first = page._stub.calls

        # 같은 PDF 재오픈 → 디스크/메모리 캐시 히트, 추가 호출 0
        page._analyses.clear()
        page.pdf.open(sample_pdf)
        page.ocr_worker.new_generation()
        page._show_page(0)
        page._submit_prefetch_jobs()
        assert _wait_until(qapp, lambda: len(page._analyses) == 3)
        assert page._stub.calls == calls_first


class TestAutoSave:
    def test_auto_save_writes_annotated_jpeg(self, qapp, page, sample_pdf, tmp_path):
        page.auto_save_check.setChecked(True)
        page.load_records(_records())
        page.pdf.open(sample_pdf)
        page.mode = "pdf"
        page.current_page = 0
        page.ocr_worker.new_generation()
        page._show_page(0, fit=True)
        page._submit_prefetch_jobs()
        assert _wait_until(qapp, lambda: page.current_page in page._outcomes)
        out_dir = tmp_path / "out"
        assert _wait_until(qapp, lambda: out_dir.exists() and list(out_dir.glob("*.jpg")))
        names = [p.name for p in out_dir.glob("*.jpg")]
        assert any("_25090776_" in n for n in names)
