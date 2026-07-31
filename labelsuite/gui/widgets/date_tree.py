"""제조일 선택 트리 — 연 > 월 > 일 계층, 네이티브 체크박스.

레거시 tkinter 트리의 두 가지 결함을 해소한다:
- Button-1 바인딩이 선택 갱신보다 먼저 실행되는 오프바이원 → Qt 체크박스는 자체 처리
- 연/월 노드 클릭 무반응 → ItemIsAutoTristate로 하위 일괄 선택/해제 지원
"""

from __future__ import annotations

from collections import defaultdict
from datetime import date

from PySide6.QtCore import Qt, Signal
from PySide6.QtWidgets import QTreeWidget, QTreeWidgetItem

_DATE_ROLE = Qt.ItemDataRole.UserRole


class DateTreeWidget(QTreeWidget):
    selection_changed = Signal()

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setHeaderLabel("제조일 선택")
        self.itemChanged.connect(lambda *_: self.selection_changed.emit())

    def set_dates(self, dates: list[date]) -> None:
        """날짜 목록으로 트리를 재구성한다. 기존 선택은 초기화된다(레거시 잔존 버그 수정)."""
        self.blockSignals(True)
        try:
            self.clear()
            hierarchy: dict[int, dict[int, list[date]]] = defaultdict(lambda: defaultdict(list))
            for d in dates:
                hierarchy[d.year][d.month].append(d)
            parent_flags = (Qt.ItemFlag.ItemIsEnabled | Qt.ItemFlag.ItemIsUserCheckable
                            | Qt.ItemFlag.ItemIsAutoTristate)
            for year in sorted(hierarchy):
                year_item = QTreeWidgetItem(self, [f"{year}년"])
                year_item.setFlags(parent_flags)
                year_item.setCheckState(0, Qt.CheckState.Unchecked)
                for month in sorted(hierarchy[year]):
                    month_item = QTreeWidgetItem(year_item, [f"{month}월"])
                    month_item.setFlags(parent_flags)
                    month_item.setCheckState(0, Qt.CheckState.Unchecked)
                    for d in sorted(hierarchy[year][month]):
                        day_item = QTreeWidgetItem(
                            month_item, [f"{d.day}일 ({d.strftime('%Y-%m-%d')})"])
                        day_item.setFlags(Qt.ItemFlag.ItemIsEnabled
                                          | Qt.ItemFlag.ItemIsUserCheckable)
                        day_item.setCheckState(0, Qt.CheckState.Unchecked)
                        day_item.setData(0, _DATE_ROLE, d)
                year_item.setExpanded(True)
        finally:
            self.blockSignals(False)
        self.selection_changed.emit()

    def checked_dates(self) -> set[date]:
        selected: set[date] = set()
        for i in range(self.topLevelItemCount()):
            year_item = self.topLevelItem(i)
            for j in range(year_item.childCount()):
                month_item = year_item.child(j)
                for k in range(month_item.childCount()):
                    day_item = month_item.child(k)
                    if day_item.checkState(0) == Qt.CheckState.Checked:
                        selected.add(day_item.data(0, _DATE_ROLE))
        return selected
