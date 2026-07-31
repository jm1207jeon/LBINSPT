"""검사 이력 탭 — Phase 7에서 전체 구현으로 교체되는 임시 골격."""

from __future__ import annotations

from PySide6.QtWidgets import QLabel, QVBoxLayout, QWidget

from labelsuite.core.config import AppConfig


class HistoryPage(QWidget):
    def __init__(self, config: AppConfig, parent=None):
        super().__init__(parent)
        self.config = config
        layout = QVBoxLayout(self)
        layout.addWidget(QLabel("검사 이력 탭 (구현 예정)"))
