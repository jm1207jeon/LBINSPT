"""설정 다이얼로그 스모크 — 값 왕복 저장 검증 (오프스크린)."""

import os

import pytest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
pytest.importorskip("PySide6")


@pytest.fixture(scope="module")
def qapp():
    from PySide6.QtWidgets import QApplication

    app = QApplication.instance() or QApplication([])
    yield app


def test_settings_round_trip(qapp, tmp_path):
    from labelsuite.core.config import AppConfig
    from labelsuite.gui.settings_dialog import SettingsDialog

    config = AppConfig(tmp_path / "cfg")
    dialog = SettingsDialog(config)

    dialog.shelf_life_spin.setValue(24)
    dialog.prefetch_combo.setCurrentIndex(2)          # 2페이지 앞
    dialog.aws_region_edit.setText("us-east-1")
    dialog.counts_table.item(0, 0).setText("7")       # MDR LOT → 7
    dialog._save_and_accept()

    fresh = AppConfig(tmp_path / "cfg")
    assert fresh.settings["shelf_life_months"] == 24
    assert fresh.settings["prefetch_policy"] == 2
    assert fresh.settings["aws"]["region"] == "us-east-1"
    assert fresh.standards_raw["standards"]["MDR"]["counts"]["LOT"] == 7


def test_china_mapping_edit(qapp, tmp_path):
    from labelsuite.core.config import AppConfig
    from labelsuite.gui.settings_dialog import SettingsDialog
    from PySide6.QtWidgets import QTableWidgetItem

    config = AppConfig(tmp_path / "cfg")
    dialog = SettingsDialog(config)
    row = dialog.china_table.rowCount() - 1
    dialog.china_table.setItem(row, 0, QTableWidgetItem("zzz"))
    dialog.china_table.setItem(row, 1, QTableWidgetItem("LBDX-99"))
    dialog._save_and_accept()

    fresh = AppConfig(tmp_path / "cfg")
    assert fresh.standards_raw["china_ref_mapping"]["ZZZ"] == "LBDX-99"
    assert fresh.standards_raw["china_ref_mapping"]["HEV"] == "LBDA-02"


def test_column_letter_hint():
    from labelsuite.gui.settings_dialog import _col_letter

    assert _col_letter(0) == "A"
    assert _col_letter(9) == "J"
    assert _col_letter(23) == "X"
    assert _col_letter(42) == "AQ"
