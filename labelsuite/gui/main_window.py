"""메인 윈도우 — 얇은 셸: 탭 + 상태바 + 메뉴만 담당한다.

레거시 main_window.py(2,780줄 갓클래스)와 달리 비즈니스 로직은 core에,
페이지별 UI는 각 페이지 위젯에 둔다.
"""

from __future__ import annotations

from PySide6.QtWidgets import QLabel, QMainWindow, QMessageBox, QTabWidget

from labelsuite import __version__
from labelsuite.core.config import AppConfig, data_dir
from labelsuite.core.history.db import HistoryDb
from labelsuite.core.schema import LabelRecord
from labelsuite.core.standards import StandardsBundle, load_standards
from labelsuite.gui.generator_page import GeneratorPage

TAB_GENERATOR = 0
TAB_INSPECTOR = 1
TAB_HISTORY = 2


class MainWindow(QMainWindow):
    def __init__(self, config: AppConfig):
        super().__init__()
        self.config = config
        self.standards: StandardsBundle = load_standards(config)
        self.history_db = HistoryDb(data_dir() / "history.sqlite3")
        self.setWindowTitle(f"LabelSuite v{__version__} — 통합 라벨 검사")
        self.resize(1600, 900)

        self.tabs = QTabWidget()
        self.setCentralWidget(self.tabs)

        self.generator_page = GeneratorPage(config)
        self.tabs.addTab(self.generator_page, "목록 생성")

        self.inspector_page = self._create_inspector_page()
        self.tabs.addTab(self.inspector_page, "라벨 검사")

        self.history_page = self._create_history_page()
        self.tabs.addTab(self.history_page, "검사 이력")

        # 상태바: 일반 메시지 + AWS 상태 표시등(우측 고정)
        self.aws_status = QLabel("AWS: 미확인")
        self.statusBar().addPermanentWidget(self.aws_status)

        self.generator_page.status_message.connect(self.statusBar().showMessage)
        self.generator_page.list_generated.connect(self._on_list_generated)

        self._create_menu()

    def _create_inspector_page(self):
        from labelsuite.gui.inspector_page import InspectorPage

        page = InspectorPage(self.config, self.standards, history_db=self.history_db)
        page.status_message.connect(self.statusBar().showMessage)
        page.aws_status_changed.connect(self._on_aws_status)
        return page

    def _create_history_page(self):
        from labelsuite.gui.history_page import HistoryPage

        page = HistoryPage(self.config, db=self.history_db)
        self.tabs.currentChanged.connect(
            lambda index: page.refresh() if index == TAB_HISTORY else None)
        return page

    def _create_menu(self) -> None:
        menu = self.menuBar().addMenu("설정")
        action = menu.addAction("환경설정…")
        action.triggered.connect(self._open_settings)
        about = self.menuBar().addMenu("도움말").addAction("정보")
        about.triggered.connect(self._show_about)

    def _open_settings(self) -> None:
        from labelsuite.gui.settings_dialog import SettingsDialog

        dialog = SettingsDialog(self.config, self)
        if dialog.exec():
            self.standards = load_standards(self.config)
            self.inspector_page.apply_config()

    def _show_about(self) -> None:
        QMessageBox.about(
            self, "LabelSuite",
            f"LabelSuite v{__version__}\n"
            "검사 목록 생성 + 라벨 OCR/바코드 검사 통합 프로그램")

    def _on_list_generated(self, records: list[LabelRecord]) -> None:
        self.inspector_page.load_records(records)
        self.tabs.setCurrentIndex(TAB_INSPECTOR)
        self.statusBar().showMessage(f"검사 목록 {len(records)}건을 검사 탭으로 전달했습니다.")

    def _on_aws_status(self, ok: bool, text: str) -> None:
        self.aws_status.setText(text)
        self.aws_status.setStyleSheet(
            "color: #007700;" if ok else "color: #cc0000; font-weight: bold;")

    def closeEvent(self, event) -> None:  # 레거시엔 없어 카메라/문서가 미해제였음
        self.inspector_page.shutdown()
        self.history_db.close()
        super().closeEvent(event)
