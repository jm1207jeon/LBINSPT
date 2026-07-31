"""필드별 검사 결과 패널 — InspectionOutcome 모델을 직접 렌더링.

레거시는 카운트를 QLabel 텍스트에 저장했다가 역파싱했다(합불 버그의 근원).
여기서는 모델 → 뷰 단방향만 존재한다.
"""

from __future__ import annotations

from PySide6.QtGui import QColor
from PySide6.QtWidgets import (
    QHeaderView,
    QLabel,
    QTableWidget,
    QTableWidgetItem,
    QVBoxLayout,
    QWidget,
)

from labelsuite.core.inspection import InspectionOutcome

_PASS_BG = QColor(144, 238, 144)     # 연녹 (레거시 #90EE90)
_FAIL_BG = QColor(255, 228, 181)     # 연주황 (레거시 #FFE4B5)
_INFO_BG = QColor(240, 240, 240)


class ResultPanel(QWidget):
    def __init__(self, field_colors: dict[str, tuple[int, int, int, int]], parent=None):
        super().__init__(parent)
        self.field_colors = field_colors

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)

        self.status_label = QLabel("검사 대기")
        self.status_label.setStyleSheet(
            "font-size: 16px; font-weight: bold; padding: 6px;")
        layout.addWidget(self.status_label)

        self.field_table = QTableWidget(0, 3)
        self.field_table.setHorizontalHeaderLabels(["필드", "검사 값", "검출/기준"])
        self.field_table.verticalHeader().setVisible(False)
        self.field_table.horizontalHeader().setSectionResizeMode(
            1, QHeaderView.ResizeMode.Stretch)
        self.field_table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        layout.addWidget(self.field_table, stretch=3)

        layout.addWidget(QLabel("바코드 검증"))
        self.barcode_table = QTableWidget(0, 4)
        self.barcode_table.setHorizontalHeaderLabels(["심볼", "필드", "바코드 값", "일치"])
        self.barcode_table.verticalHeader().setVisible(False)
        self.barcode_table.horizontalHeader().setSectionResizeMode(
            2, QHeaderView.ResizeMode.Stretch)
        self.barcode_table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        layout.addWidget(self.barcode_table, stretch=2)

    def set_field_colors(self, colors: dict[str, tuple[int, int, int, int]]) -> None:
        self.field_colors = colors

    def clear(self, message: str = "검사 대기") -> None:
        self.status_label.setText(message)
        self.status_label.setStyleSheet(
            "font-size: 16px; font-weight: bold; padding: 6px;")
        self.field_table.setRowCount(0)
        self.barcode_table.setRowCount(0)

    def show_outcome(self, outcome: InspectionOutcome) -> None:
        if outcome.passed:
            self.status_label.setText(f"합격 (PASSED) — 규격 {outcome.standard.name}")
            self.status_label.setStyleSheet(
                "font-size: 16px; font-weight: bold; padding: 6px;"
                "background-color: #90EE90;")
        else:
            self.status_label.setText(f"확인 필요 (CHECK) — 규격 {outcome.standard.name}")
            self.status_label.setStyleSheet(
                "font-size: 16px; font-weight: bold; padding: 6px;"
                "background-color: #FFE4B5;")

        fields = list(outcome.fields.values())
        self.field_table.setRowCount(len(fields))
        for row, result in enumerate(fields):
            name_item = QTableWidgetItem(result.field)
            rgba = self.field_colors.get(result.field)
            if rgba:
                name_item.setBackground(QColor(*rgba[:3], 120))
            value_item = QTableWidgetItem(result.term or "-")
            if result.expected is None:
                count_item = QTableWidgetItem(str(result.found))
                count_item.setBackground(_INFO_BG)
            else:
                count_item = QTableWidgetItem(f"{result.found}/{result.expected}")
                count_item.setBackground(_PASS_BG if result.passed else _FAIL_BG)
            self.field_table.setItem(row, 0, name_item)
            self.field_table.setItem(row, 1, value_item)
            self.field_table.setItem(row, 2, count_item)

        checks = outcome.barcode_checks
        self.barcode_table.setRowCount(len(checks))
        for row, check in enumerate(checks):
            self.barcode_table.setItem(row, 0, QTableWidgetItem(check.source))
            self.barcode_table.setItem(row, 1, QTableWidgetItem(check.field))
            value_item = QTableWidgetItem(
                check.barcode_value if check.matched
                else f"{check.barcode_value} (기대: {check.expected_value})")
            self.barcode_table.setItem(row, 2, value_item)
            mark = QTableWidgetItem("일치" if check.matched else "불일치")
            mark.setBackground(_PASS_BG if check.matched else _FAIL_BG)
            self.barcode_table.setItem(row, 3, mark)
        if not checks:
            self.barcode_table.setRowCount(1)
            empty = QTableWidgetItem("검출된 GS1 바코드 없음")
            self.barcode_table.setItem(0, 2, empty)
