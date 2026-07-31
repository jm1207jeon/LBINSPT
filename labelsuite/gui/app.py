"""애플리케이션 부트스트랩."""

from __future__ import annotations

import sys
from pathlib import Path

from PySide6.QtWidgets import QApplication

from labelsuite.core.config import AppConfig


def _find_legacy_ligen_config() -> Path | None:
    """실행 파일/저장소 주변에서 레거시 LiGen app_config.json을 찾는다."""
    candidates = [
        Path.cwd() / "Label Inspector_list generator" / "app_config.json",
        Path(__file__).resolve().parents[2] / "Label Inspector_list generator" / "app_config.json",
    ]
    for path in candidates:
        if path.exists():
            return path
    return None


def main() -> int:
    app = QApplication(sys.argv)
    app.setApplicationName("LabelSuite")
    app.setOrganizationName("LabelSuite")

    from labelsuite.gui.style import apply_theme

    apply_theme(app)

    config = AppConfig()
    legacy = _find_legacy_ligen_config()
    if legacy is not None:
        config.migrate_legacy_ligen_config(legacy)

    from labelsuite.gui.main_window import MainWindow

    window = MainWindow(config)
    window.show()
    return app.exec()
