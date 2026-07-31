"""앱 전역 디자인 — 밝고 깔끔한 단일 테마.

Fusion 스타일 위에 QSS를 얹는다. 색은 여기 팔레트 상수에서만 정의하고,
개별 위젯의 인라인 setStyleSheet는 상태 색(합격/불합격 등)에만 쓴다.
"""

from __future__ import annotations

from PySide6.QtWidgets import QApplication

# 팔레트
BG = "#f4f6f9"           # 창 배경
PANEL = "#ffffff"        # 패널/카드
BORDER = "#dde3ea"
TEXT = "#1f2937"
TEXT_MUTED = "#6b7280"
ACCENT = "#2563eb"       # 주요 동작
ACCENT_HOVER = "#1d4ed8"
ACCENT_SOFT = "#eff6ff"
SUCCESS = "#16a34a"
SUCCESS_BG = "#dcfce7"
WARN = "#d97706"
WARN_BG = "#fef3c7"
DANGER = "#dc2626"

QSS = f"""
* {{
    font-family: "Malgun Gothic", "Segoe UI", "Noto Sans KR", sans-serif;
    font-size: 13px;
    color: {TEXT};
}}
QMainWindow, QDialog {{ background: {BG}; }}
QWidget#page {{ background: {BG}; }}

/* ---- 탭 ---- */
QTabWidget::pane {{
    border: none;
    background: {BG};
    top: -1px;
}}
QTabBar::tab {{
    background: transparent;
    color: {TEXT_MUTED};
    padding: 10px 22px;
    margin-right: 4px;
    border: none;
    border-bottom: 3px solid transparent;
    font-size: 14px;
    font-weight: 600;
}}
QTabBar::tab:selected {{
    color: {ACCENT};
    border-bottom: 3px solid {ACCENT};
}}
QTabBar::tab:hover:!selected {{ color: {TEXT}; }}

/* ---- 그룹박스: 카드 스타일 ---- */
QGroupBox {{
    background: {PANEL};
    border: 1px solid {BORDER};
    border-radius: 10px;
    margin-top: 12px;
    padding: 14px 10px 10px 10px;
    font-weight: 700;
}}
QGroupBox::title {{
    subcontrol-origin: margin;
    left: 12px;
    top: 2px;
    padding: 0 4px;
    color: {TEXT};
}}

/* ---- 버튼 ---- */
QPushButton {{
    background: {PANEL};
    border: 1px solid {BORDER};
    border-radius: 7px;
    padding: 7px 14px;
    font-weight: 600;
}}
QPushButton:hover {{ background: {ACCENT_SOFT}; border-color: {ACCENT}; }}
QPushButton:pressed {{ background: #e0e9ff; }}
QPushButton:disabled {{ color: #b3b9c2; background: #f0f1f4; border-color: {BORDER}; }}
QPushButton[accent="true"] {{
    background: {ACCENT};
    color: white;
    border: none;
}}
QPushButton[accent="true"]:hover {{ background: {ACCENT_HOVER}; }}
QPushButton[accent="true"]:disabled {{ background: #b9ccf5; color: #f0f4ff; }}

/* ---- 입력 ---- */
QLineEdit, QComboBox, QSpinBox, QDoubleSpinBox {{
    background: {PANEL};
    border: 1px solid {BORDER};
    border-radius: 7px;
    padding: 6px 8px;
    selection-background-color: {ACCENT};
}}
QLineEdit:focus, QComboBox:focus, QSpinBox:focus, QDoubleSpinBox:focus {{
    border: 1px solid {ACCENT};
}}
QComboBox::drop-down {{ border: none; width: 22px; }}
QComboBox QAbstractItemView {{
    background: {PANEL};
    border: 1px solid {BORDER};
    selection-background-color: {ACCENT_SOFT};
    selection-color: {TEXT};
}}

/* ---- 테이블/트리 ---- */
QTableWidget, QTableView, QTreeWidget, QTextEdit {{
    background: {PANEL};
    border: 1px solid {BORDER};
    border-radius: 8px;
    gridline-color: #eef1f5;
    alternate-background-color: #fafbfd;
}}
QHeaderView::section {{
    background: #f8fafc;
    color: {TEXT_MUTED};
    border: none;
    border-bottom: 1px solid {BORDER};
    padding: 6px 8px;
    font-weight: 700;
}}
QTableWidget::item, QTableView::item {{ padding: 3px; }}
QTreeWidget::item {{ height: 26px; }}

/* ---- 스크롤/뷰어 ---- */
QScrollArea {{ border: 1px solid {BORDER}; border-radius: 8px; background: #eceff3; }}
QScrollBar:vertical {{ background: transparent; width: 10px; margin: 2px; }}
QScrollBar:horizontal {{ background: transparent; height: 10px; margin: 2px; }}
QScrollBar::handle {{ background: #c3cad4; border-radius: 4px; min-height: 30px; }}
QScrollBar::handle:hover {{ background: #a8b1bd; }}
QScrollBar::add-line, QScrollBar::sub-line {{ height: 0; width: 0; }}

/* ---- 기타 ---- */
QStatusBar {{ background: {PANEL}; border-top: 1px solid {BORDER}; }}
QStatusBar QLabel {{ padding: 2px 8px; }}
QMenuBar {{ background: {BG}; }}
QMenuBar::item {{ padding: 6px 10px; border-radius: 6px; }}
QMenuBar::item:selected {{ background: {ACCENT_SOFT}; }}
QMenu {{ background: {PANEL}; border: 1px solid {BORDER}; border-radius: 8px; }}
QMenu::item {{ padding: 6px 24px; border-radius: 6px; }}
QMenu::item:selected {{ background: {ACCENT_SOFT}; }}
QSplitter::handle {{ background: {BG}; width: 6px; }}
QSlider::groove:horizontal {{
    height: 4px; background: {BORDER}; border-radius: 2px;
}}
QSlider::handle:horizontal {{
    width: 14px; height: 14px; margin: -5px 0;
    background: {ACCENT}; border-radius: 7px;
}}
QCheckBox::indicator {{ width: 16px; height: 16px; }}
QToolTip {{
    background: {TEXT}; color: white; border: none;
    padding: 5px 8px; border-radius: 5px;
}}
"""

# 상태 배지용 인라인 스타일 (위젯 코드에서 재사용)
BADGE_PASS = (f"background: {SUCCESS_BG}; color: {SUCCESS}; font-weight: 700;"
              "border-radius: 8px; padding: 8px 12px; font-size: 15px;")
BADGE_FAIL = (f"background: {WARN_BG}; color: {WARN}; font-weight: 700;"
              "border-radius: 8px; padding: 8px 12px; font-size: 15px;")
BADGE_IDLE = (f"background: #eef1f5; color: {TEXT_MUTED}; font-weight: 600;"
              "border-radius: 8px; padding: 8px 12px; font-size: 14px;")
STATUS_OK = f"color: {SUCCESS}; font-weight: 600;"
STATUS_ERROR = f"color: {DANGER}; font-weight: 700;"
STATUS_MUTED = f"color: {TEXT_MUTED};"


def apply_theme(app: QApplication) -> None:
    app.setStyle("Fusion")
    app.setStyleSheet(QSS)
