"""웹캠 캡처 워커 — 레거시 camera_handler의 QTimer 이중 구동을 제거한 단일 드라이버."""

from __future__ import annotations

import time

import cv2
import numpy as np
from PySide6.QtCore import QThread, Signal


def list_cameras(max_index: int = 5) -> list[int]:
    available = []
    for index in range(max_index):
        cap = cv2.VideoCapture(index)
        if cap.isOpened():
            available.append(index)
        cap.release()
    return available


class CameraWorker(QThread):
    frame_ready = Signal(object)      # np.ndarray RGB
    failed = Signal(str)

    def __init__(self, index: int = 0, width: int = 1280, height: int = 720, parent=None):
        super().__init__(parent)
        self.index = index
        self.width = width
        self.height = height
        self._running = False

    def run(self) -> None:
        cap = cv2.VideoCapture(self.index)
        if not cap.isOpened():
            self.failed.emit(f"카메라 {self.index}번을 열 수 없습니다.")
            return
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, self.width)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, self.height)
        self._running = True
        try:
            while self._running:
                ok, frame_bgr = cap.read()
                if not ok:
                    self.failed.emit("카메라 프레임을 읽지 못했습니다.")
                    break
                self.frame_ready.emit(cv2.cvtColor(frame_bgr, cv2.COLOR_BGR2RGB))
                time.sleep(1 / 30)
        finally:
            cap.release()

    def stop(self) -> None:
        self._running = False
        self.wait(2000)
