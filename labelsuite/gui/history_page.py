"""검사 이력 탭 — SQLite 이력 조회/필터/리포트 내보내기."""

from __future__ import annotations

import os
import subprocess
import sys

from PySide6.QtCore import Qt
from PySide6.QtGui import QColor
from PySide6.QtWidgets import (
    QComboBox,
    QFileDialog,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QLineEdit,
    QMessageBox,
    QPushButton,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from labelsuite.core.config import AppConfig
from labelsuite.core.history.db import HistoryDb
from labelsuite.core.history.report import export_lot_report

_PASS_BG = QColor(144, 238, 144)
_FAIL_BG = QColor(255, 228, 181)

_COLUMNS = ["일시", "LOT", "REF", "PN", "규격", "소스", "페이지", "판정"]


class HistoryPage(QWidget):
    def __init__(self, config: AppConfig, db: HistoryDb | None = None, parent=None):
        super().__init__(parent)
        self.setObjectName("page")
        self.config = config
        self.db = db
        self._rows = []
        self._build_ui()

    def set_db(self, db: HistoryDb) -> None:
        self.db = db
        self.refresh()

    def _build_ui(self) -> None:
        layout = QVBoxLayout(self)
        layout.setContentsMargins(12, 12, 12, 12)
        layout.setSpacing(8)

        filters = QHBoxLayout()
        filters.addWidget(QLabel("LOT:"))
        self.lot_filter = QLineEdit()
        self.lot_filter.setPlaceholderText("LOT 검색")
        self.lot_filter.returnPressed.connect(self.refresh)
        filters.addWidget(self.lot_filter)
        filters.addWidget(QLabel("판정:"))
        self.verdict_filter = QComboBox()
        self.verdict_filter.addItems(["전체", "합격", "확인 필요"])
        self.verdict_filter.currentIndexChanged.connect(lambda _=0: self.refresh())
        filters.addWidget(self.verdict_filter)
        refresh_button = QPushButton("새로고침")
        refresh_button.clicked.connect(self.refresh)
        filters.addWidget(refresh_button)
        filters.addStretch(1)
        report_button = QPushButton("LOT 리포트 내보내기")
        report_button.clicked.connect(self._export_report)
        filters.addWidget(report_button)
        layout.addLayout(filters)

        self.table = QTableWidget(0, len(_COLUMNS))
        self.table.setHorizontalHeaderLabels(_COLUMNS)
        self.table.setAlternatingRowColors(True)
        self.table.verticalHeader().setVisible(False)
        self.table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        self.table.setSelectionBehavior(QTableWidget.SelectionBehavior.SelectRows)
        self.table.horizontalHeader().setSectionResizeMode(
            0, QHeaderView.ResizeMode.ResizeToContents)
        self.table.itemDoubleClicked.connect(self._open_image)
        layout.addWidget(self.table)

        hint = QLabel("행을 더블클릭하면 저장된 결과 이미지를 엽니다.")
        hint.setStyleSheet("color: gray;")
        layout.addWidget(hint)

    def refresh(self) -> None:
        if self.db is None:
            return
        verdict = {"전체": None, "합격": True, "확인 필요": False}[
            self.verdict_filter.currentText()]
        self._rows = self.db.query(lot=self.lot_filter.text().strip() or None,
                                   passed=verdict)
        self.table.setRowCount(len(self._rows))
        for i, row in enumerate(self._rows):
            values = [row.ts.replace("T", " "), row.lot, row.ref, row.pn,
                      row.standard, row.source,
                      str(row.page + 1) if row.page is not None else "",
                      "합격" if row.passed else "확인 필요"]
            for j, value in enumerate(values):
                item = QTableWidgetItem(value)
                if j == len(values) - 1:
                    item.setBackground(_PASS_BG if row.passed else _FAIL_BG)
                item.setData(Qt.ItemDataRole.UserRole, row.image_path)
                self.table.setItem(i, j, item)

    def _open_image(self, item: QTableWidgetItem) -> None:
        path = item.data(Qt.ItemDataRole.UserRole)
        if not path or not os.path.exists(path):
            QMessageBox.information(self, "이미지 없음",
                                    "저장된 이미지 파일을 찾을 수 없습니다.")
            return
        if sys.platform.startswith("win"):
            os.startfile(path)  # noqa: S606
        elif sys.platform == "darwin":
            subprocess.Popen(["open", path])
        else:
            subprocess.Popen(["xdg-open", path])

    def _export_report(self) -> None:
        if self.db is None:
            return
        lots = self.db.lots()
        if not lots:
            QMessageBox.information(self, "리포트", "저장된 검사 이력이 없습니다.")
            return
        lot = self.lot_filter.text().strip()
        if lot not in lots:
            lot = lots[0] if len(lots) == 1 else ""
        if not lot:
            QMessageBox.information(
                self, "리포트",
                "LOT 필터에 리포트를 만들 LOT을 입력하세요.\n"
                f"보유 LOT: {', '.join(lots[:10])}{' …' if len(lots) > 10 else ''}")
            return
        default = os.path.join(
            self.config.settings.get("save_directory") or os.path.expanduser("~"),
            f"검사리포트_{lot}.xlsx")
        path, _ = QFileDialog.getSaveFileName(self, "리포트 저장", default,
                                              "Excel 파일 (*.xlsx)")
        if not path:
            return
        count = export_lot_report(self.db, lot, path)
        QMessageBox.information(self, "리포트 완료",
                                f"LOT {lot} 검사 {count}건을 내보냈습니다.\n{path}")
