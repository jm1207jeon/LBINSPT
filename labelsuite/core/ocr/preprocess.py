"""이미지 전처리 — 자동 스큐 보정 (레거시 auto_skew_correction 이식).

레거시에는 동일한 로직이 2벌(auto/manual) 있었고 manual 쪽은 호출자가 없었다.
여기 단일 구현만 유지한다.
"""

from __future__ import annotations

import cv2
import numpy as np

PREPROCESS_SIGNATURE = "skew-v1"  # OCR 캐시 키에 포함 — 로직 변경 시 갱신할 것


def auto_skew_correction(image: np.ndarray) -> np.ndarray:
    """텍스트 라인 기울기를 감지해 0.5도 초과 시 보정한 이미지를 반환한다."""
    try:
        gray = cv2.cvtColor(image, cv2.COLOR_RGB2GRAY) if image.ndim == 3 else image.copy()

        # 1차: Hough 변환으로 수평선 각도 수집
        edges = cv2.Canny(gray, 50, 150, apertureSize=3)
        lines = cv2.HoughLines(edges, 1, np.pi / 180, threshold=100)
        angles: list[float] = []
        if lines is not None:
            for line in lines[:20]:
                _, theta = line[0]
                angle = theta * 180 / np.pi - 90
                if abs(angle) < 45:
                    angles.append(angle)

        # 2차: 텍스트 컨투어 분석 폴백
        if not angles:
            kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (20, 1))
            dilated = cv2.dilate(gray, kernel, iterations=1)
            contours, _ = cv2.findContours(dilated, cv2.RETR_EXTERNAL,
                                           cv2.CHAIN_APPROX_SIMPLE)
            for contour in contours:
                if cv2.contourArea(contour) > 500:
                    angle = cv2.minAreaRect(contour)[2]
                    if angle < -45:
                        angle = 90 + angle
                    elif angle > 45:
                        angle = angle - 90
                    if abs(angle) < 45:
                        angles.append(angle)

        if not angles:
            return image
        median_angle = float(np.median(angles))
        if abs(median_angle) <= 0.5:
            return image

        h, w = image.shape[:2]
        center = (w // 2, h // 2)
        matrix = cv2.getRotationMatrix2D(center, median_angle, 1.0)
        cos = abs(matrix[0, 0])
        sin = abs(matrix[0, 1])
        new_w = int(h * sin + w * cos)
        new_h = int(h * cos + w * sin)
        matrix[0, 2] += new_w / 2 - center[0]
        matrix[1, 2] += new_h / 2 - center[1]
        return cv2.warpAffine(image, matrix, (new_w, new_h),
                              flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE)
    except cv2.error:
        return image
