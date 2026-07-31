"""환경설정 다이얼로그 — Phase 8에서 전체 구현으로 교체되는 임시 골격."""

from __future__ import annotations

from PySide6.QtWidgets import QDialog, QDialogButtonBox, QLabel, QVBoxLayout

from labelsuite.core.config import AppConfig


class SettingsDialog(QDialog):
    def __init__(self, config: AppConfig, parent=None):
        super().__init__(parent)
        self.config = config
        self.setWindowTitle("환경설정")
        layout = QVBoxLayout(self)
        layout.addWidget(QLabel("설정 UI (구현 예정)"))
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Close)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)
