"""줌/팬 이미지 뷰어 — 레거시 zoomable_scroll_area의 검증된 조작감을 PySide6로 이식.

휠 줌(마우스 앵커), 드래그 팬, 0.1~5.0 배율, 0.3 스텝.
레거시의 상시 True 조건문·빈 스텁(centerOn)·이미지 재렌더 의존은 제거하고
베이스 이미지를 위젯이 직접 보관해 스케일한다.
"""

from __future__ import annotations

import numpy as np
from PySide6.QtCore import QPoint, Qt, Signal
from PySide6.QtGui import QCursor, QImage, QPixmap
from PySide6.QtWidgets import QLabel, QScrollArea

MIN_ZOOM = 0.1
MAX_ZOOM = 5.0
ZOOM_STEP = 0.3


class ZoomableImageView(QScrollArea):
    zoom_changed = Signal(int)   # 퍼센트

    def __init__(self, parent=None):
        super().__init__(parent)
        self.zoom_factor = 1.0
        self._panning = False
        self._last_pan_point = QPoint()
        self._base_image: np.ndarray | None = None
        self._base_pixmap: QPixmap | None = None

        self._label = QLabel("LIVE CAM 또는 PDF를 선택하세요")
        self._label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.setWidget(self._label)
        self.setWidgetResizable(False)
        self.setMouseTracking(True)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)
        self.setVerticalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAsNeeded)

    # ---------- 이미지 ----------

    def set_image(self, image_rgb: np.ndarray | None, *, fit: bool = False) -> None:
        """RGB ndarray 표시. fit=True면 현재 뷰포트에 맞춰 초기 배율 설정."""
        self._base_image = image_rgb
        if image_rgb is None:
            self._base_pixmap = None
            self._label.setPixmap(QPixmap())
            self._label.setText("LIVE CAM 또는 PDF를 선택하세요")
            self._label.adjustSize()
            return
        image = np.ascontiguousarray(image_rgb)
        height, width = image.shape[:2]
        qimage = QImage(image.data, width, height, width * 3, QImage.Format.Format_RGB888)
        self._base_pixmap = QPixmap.fromImage(qimage.copy())
        if fit:
            viewport = self.viewport().size()
            if width and height:
                self.zoom_factor = max(MIN_ZOOM, min(
                    viewport.width() / width, viewport.height() / height, 1.0))
        self._apply_zoom()

    def show_message(self, text: str) -> None:
        self._base_image = None
        self._base_pixmap = None
        self._label.setPixmap(QPixmap())
        self._label.setText(text)
        self._label.adjustSize()

    def _apply_zoom(self) -> None:
        if self._base_pixmap is None:
            return
        size = self._base_pixmap.size() * self.zoom_factor
        mode = (Qt.TransformationMode.SmoothTransformation
                if self.zoom_factor < 2.0 else Qt.TransformationMode.FastTransformation)
        self._label.setPixmap(self._base_pixmap.scaled(
            size, Qt.AspectRatioMode.KeepAspectRatio, mode))
        self._label.adjustSize()
        self.zoom_changed.emit(int(self.zoom_factor * 100))

    def set_zoom_percent(self, percent: int) -> None:
        factor = max(MIN_ZOOM, min(percent / 100.0, MAX_ZOOM))
        if abs(factor - self.zoom_factor) > 1e-3:
            self.zoom_factor = factor
            self._apply_zoom()

    # ---------- 휠 줌 (마우스 앵커 보정 — 레거시 로직 유지) ----------

    def wheelEvent(self, event) -> None:
        if self._base_pixmap is None:
            super().wheelEvent(event)
            return
        h_bar = self.horizontalScrollBar()
        v_bar = self.verticalScrollBar()
        old_h, old_v = h_bar.value(), v_bar.value()
        mouse_pos = event.position().toPoint()

        old_zoom = self.zoom_factor
        if event.angleDelta().y() > 0:
            new_zoom = min(old_zoom + ZOOM_STEP, MAX_ZOOM)
        else:
            new_zoom = max(old_zoom - ZOOM_STEP, MIN_ZOOM)
        if new_zoom == old_zoom:
            return
        self.zoom_factor = new_zoom
        self._apply_zoom()
        ratio = new_zoom / old_zoom
        h_bar.setValue(int((old_h + mouse_pos.x()) * ratio - mouse_pos.x()))
        v_bar.setValue(int((old_v + mouse_pos.y()) * ratio - mouse_pos.y()))
        event.accept()

    # ---------- 드래그 팬 ----------

    def mousePressEvent(self, event) -> None:
        if event.button() == Qt.MouseButton.LeftButton and self._base_pixmap is not None:
            self._panning = True
            self._last_pan_point = event.position().toPoint()
            self.setCursor(QCursor(Qt.CursorShape.ClosedHandCursor))
            event.accept()
            return
        super().mousePressEvent(event)

    def mouseMoveEvent(self, event) -> None:
        if self._panning and (event.buttons() & Qt.MouseButton.LeftButton):
            delta = event.position().toPoint() - self._last_pan_point
            self.horizontalScrollBar().setValue(self.horizontalScrollBar().value() - delta.x())
            self.verticalScrollBar().setValue(self.verticalScrollBar().value() - delta.y())
            self._last_pan_point = event.position().toPoint()
            event.accept()
            return
        if self._base_pixmap is not None:
            self.setCursor(QCursor(Qt.CursorShape.OpenHandCursor))
        super().mouseMoveEvent(event)

    def mouseReleaseEvent(self, event) -> None:
        if event.button() == Qt.MouseButton.LeftButton and self._panning:
            self._panning = False
            self.setCursor(QCursor(Qt.CursorShape.OpenHandCursor
                                   if self._base_pixmap is not None
                                   else Qt.CursorShape.ArrowCursor))
        super().mouseReleaseEvent(event)

    def leaveEvent(self, event) -> None:
        self.setCursor(QCursor(Qt.CursorShape.ArrowCursor))
        super().leaveEvent(event)
