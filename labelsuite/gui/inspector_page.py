"""라벨 검사 탭 — Phase 5에서 전체 구현으로 교체되는 임시 골격."""

from __future__ import annotations

from PySide6.QtCore import Signal
from PySide6.QtWidgets import QLabel, QVBoxLayout, QWidget

from labelsuite.core.config import AppConfig
from labelsuite.core.schema import LabelRecord
from labelsuite.core.standards import StandardsBundle


class InspectorPage(QWidget):
    status_message = Signal(str)
    aws_status_changed = Signal(bool, str)

    def __init__(self, config: AppConfig, standards: StandardsBundle, parent=None):
        super().__init__(parent)
        self.config = config
        self.standards = standards
        self.records: list[LabelRecord] = []
        layout = QVBoxLayout(self)
        self._placeholder = QLabel("라벨 검사 탭 (구현 예정)")
        layout.addWidget(self._placeholder)

    def load_records(self, records: list[LabelRecord]) -> None:
        self.records = records
        self._placeholder.setText(f"검사 목록 {len(records)}건 수신 (검사 UI 구현 예정)")

    def apply_config(self) -> None:
        pass

    def shutdown(self) -> None:
        pass
