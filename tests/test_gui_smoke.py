"""GUI 스모크 테스트 — 오프스크린 플랫폼에서 위젯 생성/핸드오프만 검증."""

import os
from datetime import date

import pytest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

pytest.importorskip("PySide6")


@pytest.fixture(scope="module")
def qapp():
    from PySide6.QtWidgets import QApplication

    app = QApplication.instance() or QApplication([])
    yield app


@pytest.fixture
def config(tmp_path):
    from labelsuite.core.config import AppConfig

    return AppConfig(tmp_path / "cfg")


def test_main_window_builds(qapp, config):
    from labelsuite.gui.main_window import MainWindow

    window = MainWindow(config)
    assert window.tabs.count() == 3
    assert window.tabs.tabText(0) == "목록 생성"
    window.close()


def test_date_tree_check_and_parent_toggle(qapp):
    from PySide6.QtCore import Qt

    from labelsuite.gui.widgets.date_tree import DateTreeWidget

    tree = DateTreeWidget()
    tree.set_dates([date(2024, 5, 10), date(2024, 5, 11), date(2024, 6, 1)])
    assert tree.checked_dates() == set()

    # 일 노드 하나 체크
    may = tree.topLevelItem(0).child(0)
    may.child(0).setCheckState(0, Qt.CheckState.Checked)
    assert tree.checked_dates() == {date(2024, 5, 10)}

    # 월 노드 체크 → 하위 일 전체 선택 (레거시 연/월 무반응 개선)
    may.setCheckState(0, Qt.CheckState.Checked)
    assert tree.checked_dates() == {date(2024, 5, 10), date(2024, 5, 11)}

    # 재구성 시 선택 초기화 (레거시 잔존 선택 버그 수정)
    tree.set_dates([date(2025, 1, 1)])
    assert tree.checked_dates() == set()


def test_handoff_signal(qapp, config):
    from labelsuite.core.schema import LabelRecord
    from labelsuite.gui.main_window import MainWindow

    window = MainWindow(config)
    records = [LabelRecord("L1", "P", "PN", "REF", "2024-05-10", "2027-05-09",
                           "08806173612345", standard="MDR")]
    window.generator_page.list_generated.emit(records)
    assert window.tabs.currentIndex() == 1
    assert window.inspector_page.records == records
    window.close()
