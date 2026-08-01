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
    """3페이지 라벨 PDF — 페이지마다 폭을 다르게 해 스텁이 페이지를 식별한다."""
    path = tmp_path / "labels.pdf"
    doc = fitz.open()
    for i in range(3):
        page = doc.new_page(width=400 + i * 10, height=300)
        page.insert_text((50, 100), f"LOT 2509077{i}")
        page.insert_text((50, 130), "REF NCN20-080-230")
    doc.save(str(path))
    doc.close()
    return str(path)


class StubTextract:
    """호출 횟수를 세는 가짜 Textract — 페이지(이미지 폭)별로 다른 LOT을 돌려준다."""

    def __init__(self, render_zoom=4.0):
        self.calls = 0
        self.render_zoom = render_zoom

    def detect_words(self, image_rgb):
        from labelsuite.core.ocr.textract_client import OcrWord

        self.calls += 1
        page_index = round((image_rgb.shape[1] / self.render_zoom - 400) / 10)
        page_index = max(0, min(2, page_index))
        return [OcrWord(f"2509077{page_index}", (10, 10, 50, 12), 95),
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


def _records():
    from labelsuite.core.schema import LabelRecord

    return [
        LabelRecord(f"2509077{i}", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
                    "2024-05-10", "2027-05-09", "08806173612345", standard="MDR")
        for i in range(3)
    ]


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
        assert page.lot_combo.currentText() == "25090770"
        assert page._current_standard_name() == "MDR"
        assert page.standard_combo.currentText() == "PML-001(Rev.1)"  # 표시명

    def test_lot_follows_page_navigation(self, qapp, page, sample_pdf):
        """다페이지 PDF에서 페이지를 넘기면 그 페이지의 LOT이 자동 선택돼야 한다."""
        page.load_records(_records())
        page.pdf.open(sample_pdf)
        page.mode = "pdf"
        page.current_page = 0
        page.ocr_worker.new_generation()
        page._show_page(0, fit=True)
        page._submit_prefetch_jobs()
        assert _wait_until(qapp, lambda: len(page._analyses) == 3)

        page._navigate(1)
        qapp.processEvents()
        assert page.lot_combo.currentText() == "25090771"
        page._navigate(1)
        qapp.processEvents()
        assert page.lot_combo.currentText() == "25090772"
        page._navigate("first")
        qapp.processEvents()
        assert page.lot_combo.currentText() == "25090770"

    def test_manual_lot_choice_sticks_on_its_page(self, qapp, page, sample_pdf):
        """사용자가 직접 고른 페이지는 자동 매칭이 덮어쓰지 않는다."""
        page.load_records(_records())
        page.pdf.open(sample_pdf)
        page.mode = "pdf"
        page.current_page = 0
        page.ocr_worker.new_generation()
        page._show_page(0, fit=True)
        page._submit_prefetch_jobs()
        assert _wait_until(qapp, lambda: len(page._analyses) == 3)

        # 0페이지에서 수동으로 다른 LOT 선택 (시그널 경유 → 수동으로 기록됨)
        page.lot_combo.setCurrentIndex(3)   # 25090772
        qapp.processEvents()
        assert page.lot_combo.currentText() == "25090772"
        assert 0 in page._manual_lot_pages

        # 다른 페이지는 여전히 자동, 수동 페이지로 돌아오면 수동 선택 유지
        page._navigate(1)
        qapp.processEvents()
        assert page.lot_combo.currentText() == "25090771"
        page._navigate("first")
        qapp.processEvents()
        assert page.lot_combo.currentText() == "25090772"

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
        assert any("_25090770_" in n for n in names)
