import sys
import os
from PyQt5.QtWidgets import (QMainWindow, QWidget, QVBoxLayout, QHBoxLayout, 
                             QPushButton, QLabel, QSlider, QComboBox, QLineEdit, 
                             QProgressBar, QFrame, QSplitter, QFileDialog, QMessageBox,
                             QDialog, QFormLayout, QSpinBox, QCheckBox, QDoubleSpinBox,
                             QGridLayout, QTextEdit)
from PyQt5.QtCore import Qt, pyqtSlot
from PyQt5.QtGui import QPixmap, QFont
import pandas as pd
from PyQt5.QtCore import Qt, QThread, pyqtSignal, pyqtSlot, QTimer, QRect
from PyQt5.QtGui import QPixmap, QImage, QPainter, QPen, QColor, QFont, QCursor
import cv2
import numpy as np
from core.camera_handler import CameraHandler
from core.pdf_handler import PDFHandler
from core.aws_textract_engine import AWSTextractEngine
from core.barcode_detector import BarcodeDetector
from gui.zoomable_scroll_area import ZoomableScrollArea
from gui.draggable_label import DraggableLabel

class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle("Label Inspector")
        self.setGeometry(100, 100, 1800, 800)
        
        # Initialize handlers
        self.camera_handler = CameraHandler()
        self.pdf_handler = PDFHandler()
        self.aws_textract_engine = AWSTextractEngine()
        self.barcode_detector = BarcodeDetector()
        
        # Camera state management
        self.is_camera_frozen = False
        self.frozen_image = None
        
        # Enable keyboard focus for key events
        self.setFocusPolicy(Qt.StrongFocus)
        
        # OCR engine selection
        self.current_ocr_engine = 'aws_textract'  # Default to AWS TEXTRACT
        
        # State variables
        self.current_mode = None  # 'camera' or 'pdf'
        self.current_image = None
        self.save_path = os.path.expanduser("~/Desktop")
        # Define field colors
        self.field_colors = {
            'LOT': QColor(255, 0, 0, 100),      # Red
            'PRODUCTS': QColor(128, 128, 128, 100), # Gray
            'PN': QColor(0, 0, 255, 100),       # Blue
            'REF': QColor(0, 255, 0, 100),      # Green
            'MFG DATE': QColor(255, 255, 0, 100), # Yellow
            'EXP DATE': QColor(255, 0, 255, 100),  # Magenta
            'GTIN': QColor(0, 255, 255, 100),   # Cyan
            'CHINA': QColor(255, 165, 0, 100)   # Orange
        }
        self.search_terms = {}
        self.ocr_results = []
        self.barcode_results = []
        
        # File counter for sequential naming
        self.file_counter = 1
        
        # Auto save toggle state
        self.auto_save_enabled = False
        
        # Inspection standards
        self.reference_counts = {
            'MDR': {'LOT': 11, 'PN': 3, 'REF': 11, 'MFG DATE': 2, 'EXP DATE': 3, 'GTIN': 8, 'CHINA': 0},
            'MDD': {'LOT': 11, 'PN': 3, 'REF': 11, 'MFG DATE': 2, 'EXP DATE': 3, 'GTIN': 8, 'CHINA': 0},
            'BSC': {'LOT': 13, 'PN': 4, 'REF': 4, 'MFG DATE': 2, 'EXP DATE': 2, 'GTIN': 1, 'CHINA': 0},
            'A00': {'LOT': 13, 'PN': 3, 'REF': 13, 'MFG DATE': 2, 'EXP DATE': 2, 'GTIN': 1, 'CHINA': 0},
            'A02': {'LOT': 13, 'PN': 3, 'REF': 13, 'MFG DATE': 2, 'EXP DATE': 4, 'GTIN': 1, 'CHINA': 0},
            '중국': {'LOT': 5, 'PN': 0, 'REF': 5, 'MFG DATE': 1, 'EXP DATE': 5, 'GTIN': 4, 'CHINA': 1}
        }
        
        # REF to CHINA field mapping for 중국 label type
        self.china_ref_mapping = {
            'HEV': 'LBDA-02',
            'NDS': 'LBDC-01', 
            'NES': 'LBDA-01',
            'SHS': 'LBDB-01',
            'TLC': 'LBDC-02',
            'EPF': 'LBDA-03',
            'EPE': 'LBDA-04',
            'BCL': 'LBDB-02',
            'BCA': 'LBDB-03',
            'BPJ': 'LBDB-04'
        }
        self.current_standard = None
        
        self.setup_ui()
        self.setup_connections()
        self.setup_keyboard_shortcuts()
        
    def setup_ui(self):
        central_widget = QWidget()
        self.setCentralWidget(central_widget)
        
        # Main horizontal splitter (3:7 ratio)
        main_splitter = QSplitter(Qt.Horizontal)
        main_splitter.setSizes([576, 1344])  # 3:7 ratio for 1920px width
        
        # Left panel
        left_panel = self.create_left_panel()
        main_splitter.addWidget(left_panel)
        
        # Right panel
        right_panel = self.create_right_panel()
        main_splitter.addWidget(right_panel)
        
        # Main layout
        main_layout = QHBoxLayout()
        main_layout.addWidget(main_splitter)
        central_widget.setLayout(main_layout)
        
    def create_left_panel(self):
        left_widget = QFrame()
        left_widget.setFrameStyle(QFrame.StyledPanel)
        left_widget.setMaximumWidth(400)
        left_widget.setMinimumWidth(250)
        
        layout = QVBoxLayout()
        
        
        # Mode selection buttons
        mode_layout = QHBoxLayout()
        self.live_cam_btn = QPushButton("LIVE CAM")
        self.pdf_view_btn = QPushButton("PDF VIEW")
        self.live_cam_btn.setCheckable(True)
        self.pdf_view_btn.setCheckable(True)
        mode_layout.addWidget(self.live_cam_btn)
        mode_layout.addWidget(self.pdf_view_btn)
        layout.addLayout(mode_layout)
        
        # Camera and PDF controls
        controls_layout = QHBoxLayout()
        self.cam_set_btn = QPushButton("CAM SET")
        self.pdf_load_btn = QPushButton("PDF Load")
        controls_layout.addWidget(self.cam_set_btn)
        controls_layout.addWidget(self.pdf_load_btn)
        layout.addLayout(controls_layout)
        
        # CAPTURE+OCR and REFRESH buttons (only visible in camera mode)
        camera_buttons_layout = QHBoxLayout()
        
        self.capture_ocr_btn = QPushButton("CAPTURE+OCR")
        self.capture_ocr_btn.setStyleSheet("""
            QPushButton {
                background-color: #FF9800;
                color: white;
                border: none;
                border-radius: 4px;
                padding: 8px 16px;
                font-weight: bold;
                min-width: 120px;
            }
            QPushButton:hover {
                background-color: #F57C00;
            }
            QPushButton:pressed {
                background-color: #E65100;
            }
        """)
        
        self.refresh_btn = QPushButton("REFRESH")
        self.refresh_btn.setStyleSheet("""
            QPushButton {
                background-color: #4CAF50;
                color: white;
                border: none;
                border-radius: 4px;
                padding: 8px 16px;
                font-weight: bold;
                min-width: 100px;
            }
            QPushButton:hover {
                background-color: #45a049;
            }
            QPushButton:pressed {
                background-color: #2E7D32;
            }
        """)
        
        camera_buttons_layout.addWidget(self.capture_ocr_btn)
        camera_buttons_layout.addWidget(self.refresh_btn)
        
        self.capture_ocr_btn.setVisible(False)  # Hidden by default
        self.refresh_btn.setVisible(False)  # Hidden by default
        layout.addLayout(camera_buttons_layout)
        
        # PDF filename display
        self.pdf_name_label = QLabel("No PDF loaded")
        self.pdf_name_label.setStyleSheet("color: gray; font-style: italic;")
        layout.addWidget(self.pdf_name_label)
        
        # Inspection List section
        inspection_frame = QFrame()
        inspection_frame.setFrameStyle(QFrame.StyledPanel)
        inspection_layout = QVBoxLayout()
        
        # Load inspection list button and file info layout
        load_layout = QHBoxLayout()
        
        self.load_inspection_btn = QPushButton("검사 목록 Load")
        self.load_inspection_btn.clicked.connect(self.load_inspection_list)
        self.load_inspection_btn.setMaximumWidth(200)  # Match LIVE CAM button width
        load_layout.addWidget(self.load_inspection_btn)
        
        # Excel file info display (inline with load button)
        self.excel_info_label = QLabel("")
        self.excel_info_label.setStyleSheet("color: green; font-weight: bold; margin-left: 10px;")
        load_layout.addWidget(self.excel_info_label)
        
        inspection_layout.addLayout(load_layout)
        
        # Inspection standard buttons
        standards_layout = QHBoxLayout()
        standards_layout.setSpacing(5)  # Equal spacing between buttons
        
        self.standard_buttons = {}
        standard_names = ['MDR', 'MDD', 'BSC', 'A00', 'A02', '중국']
        
        for name in standard_names:
            btn = QPushButton(name)
            btn.setCheckable(True)
            btn.clicked.connect(lambda checked, std=name: self.set_inspection_standard(std))
            btn.setMinimumHeight(30)
            btn.setMaximumHeight(30)
            self.standard_buttons[name] = btn
            standards_layout.addWidget(btn)
        
        inspection_layout.addLayout(standards_layout)
        
        # Inspection data display area
        inspection_data_layout = QGridLayout()
        inspection_data_layout.setVerticalSpacing(3)  # Further reduced spacing (30% less than 4)
        inspection_data_layout.setColumnStretch(0, 0)  # Icon column (fixed)
        inspection_data_layout.setColumnStretch(1, 1)  # Label column
        inspection_data_layout.setColumnStretch(2, 4)  # Field column (wider)
        inspection_data_layout.setColumnStretch(3, 0)  # Count column (fixed)
        
        # Initialize inspection count labels storage
        self.inspection_count_labels = {}
        
        # Field colors for UI display
        field_colors = {
            'PRODUCTS': QColor(100, 100, 100),  # Gray
            'LOT': QColor(200, 0, 0),          # Red
            'PN': QColor(0, 150, 0),           # Green
            'REF': QColor(0, 100, 200),        # Blue
            'MFG DATE': QColor(200, 200, 0),   # Yellow
            'EXP DATE': QColor(150, 0, 150),   # Purple
            'GTIN': QColor(0, 200, 200)        # Cyan
        }
        
        # 1. LOT field (first)
        lot_color = field_colors['LOT']
        lot_icon = QLabel("■")
        lot_icon.setStyleSheet(f"color: rgb({lot_color.red()}, {lot_color.green()}, {lot_color.blue()}); font-weight: bold; font-size: 14px;")
        lot_icon.setFixedWidth(20)
        lot_label = QLabel("LOT:")
        lot_label.setStyleSheet("color: black; font-weight: normal;")
        lot_label.setMinimumWidth(80)
        
        # LOT dropdown with reduced width
        self.lot_combo = QComboBox()
        self.lot_combo.addItem("Select LOT...")
        self.lot_combo.currentTextChanged.connect(self.on_lot_selected)
        self.lot_combo.setMaximumWidth(120)  # Reduce width to half
        
        # LOT search label and input
        lot_search_label = QLabel("LOT 검색:")
        self.lot_search_input = QLineEdit()
        self.lot_search_input.setPlaceholderText("8자리 또는 뒤 4자리 입력")
        self.lot_search_input.setMaximumWidth(120)
        self.lot_search_input.textChanged.connect(self.on_lot_search_changed)
        
        # Create horizontal layout for LOT controls
        lot_controls_layout = QHBoxLayout()
        lot_controls_layout.addWidget(self.lot_combo)
        lot_controls_layout.addWidget(lot_search_label)
        lot_controls_layout.addWidget(self.lot_search_input)
        lot_controls_layout.addStretch()  # Add stretch to push everything left
        
        inspection_data_layout.addWidget(lot_icon, 0, 0)
        inspection_data_layout.addWidget(lot_label, 0, 1)
        inspection_data_layout.addLayout(lot_controls_layout, 0, 2)
        
        # Add count label for LOT
        lot_count_label = QLabel("0")
        lot_count_label.setFixedWidth(50)  # Increased width for "detected/reference" format
        lot_count_label.setAlignment(Qt.AlignCenter)
        lot_count_label.setStyleSheet("border: 1px solid gray; padding: 2px;")
        self.inspection_count_labels['LOT'] = lot_count_label
        inspection_data_layout.addWidget(lot_count_label, 0, 3)
        
        # Field colors for UI display
        field_colors = {
            'PRODUCTS': QColor(100, 100, 100),  # Gray
            'LOT': QColor(200, 0, 0),          # Red
            'PN': QColor(0, 150, 0),           # Green
            'REF': QColor(0, 100, 200),        # Blue
            'MFG DATE': QColor(200, 200, 0),   # Yellow
            'EXP DATE': QColor(150, 0, 150),   # Purple
            'GTIN': QColor(0, 200, 200)        # Cyan
        }
        
        # 2. PRODUCTS field (info only - no count)
        products_color = field_colors['PRODUCTS']
        products_icon = QLabel("■")
        products_icon.setStyleSheet(f"color: rgb({products_color.red()}, {products_color.green()}, {products_color.blue()}); font-weight: bold; font-size: 14px;")
        products_icon.setFixedWidth(20)
        products_label = QLabel("PRODUCTS:")
        products_label.setStyleSheet("color: black; font-weight: normal;")
        products_label.setMinimumWidth(80)
        products_value = QLabel("-")
        products_value.setStyleSheet("background-color: #f9f9f9; border: 1px solid #ddd; padding: 2px; height: 22px;")
        self.inspection_fields = {'PRODUCTS': products_value}
        inspection_data_layout.addWidget(products_icon, 1, 0)
        inspection_data_layout.addWidget(products_label, 1, 1)
        inspection_data_layout.addWidget(products_value, 1, 2)
        
        # 3-6. Other fields (read-only) - PN, REF, MFG DATE, EXP DATE
        field_names = ['PN', 'REF', 'MFG DATE', 'EXP DATE']
        
        for i, field in enumerate(field_names, 2):
            color = self.field_colors[field]
            icon = QLabel("■")
            icon.setStyleSheet(f"color: rgb({color.red()}, {color.green()}, {color.blue()}); font-weight: bold; font-size: 14px;")
            icon.setFixedWidth(20)
            label = QLabel(f"{field}:")
            label.setStyleSheet("color: black; font-weight: normal;")
            label.setMinimumWidth(80)
            value_label = QLabel("-")
            value_label.setStyleSheet("background-color: #f9f9f9; border: 1px solid #ddd; padding: 2px; height: 22px;")
            # Connect text change signal to update highlighting
            if hasattr(value_label, 'textChanged'):
                value_label.textChanged.connect(lambda: self.update_inspection_highlighting() if hasattr(self, 'ocr_results') and self.ocr_results else None)
            self.inspection_fields[field] = value_label
            
            # Add count label for each field
            count_label = QLabel("0")
            count_label.setFixedWidth(50)  # Increased width for "detected/reference" format
            count_label.setAlignment(Qt.AlignCenter)
            count_label.setStyleSheet("border: 1px solid gray; padding: 2px;")
            self.inspection_count_labels[field] = count_label
            
            inspection_data_layout.addWidget(icon, i, 0)
            inspection_data_layout.addWidget(label, i, 1)
            inspection_data_layout.addWidget(value_label, i, 2)
            inspection_data_layout.addWidget(count_label, i, 3)
        
        # 7. GTIN field
        gtin_row = len(field_names) + 2
        gtin_color = self.field_colors['GTIN']
        gtin_icon = QLabel("■")
        gtin_icon.setStyleSheet(f"color: rgb({gtin_color.red()}, {gtin_color.green()}, {gtin_color.blue()}); font-weight: bold; font-size: 14px;")
        gtin_icon.setFixedWidth(20)
        gtin_label = QLabel("GTIN:")
        gtin_label.setStyleSheet("color: black; font-weight: normal;")
        gtin_label.setMinimumWidth(80)
        
        # Create display field for GTIN (read-only)
        self.gtin_field = QLabel("-")
        self.gtin_field.setStyleSheet("height: 22px; padding: 2px; border: 1px solid #ccc; background-color: #f5f5f5;")
        self.gtin_field.setAlignment(Qt.AlignLeft | Qt.AlignVCenter)
        
        # Add count label for GTIN (always shows 1 or 0)
        gtin_count_label = QLabel("0")
        gtin_count_label.setFixedWidth(50)
        gtin_count_label.setAlignment(Qt.AlignCenter)
        gtin_count_label.setStyleSheet("border: 1px solid gray; padding: 2px;")
        self.inspection_count_labels['GTIN'] = gtin_count_label
        
        inspection_data_layout.addWidget(gtin_icon, gtin_row, 0)
        inspection_data_layout.addWidget(gtin_label, gtin_row, 1)
        inspection_data_layout.addWidget(self.gtin_field, gtin_row, 2)
        inspection_data_layout.addWidget(gtin_count_label, gtin_row, 3)
        
        # 8. CHINA field (after GTIN)
        china_row = len(field_names) + 3
        china_color = self.field_colors['CHINA']
        china_icon = QLabel("■")
        china_icon.setStyleSheet(f"color: rgb({china_color.red()}, {china_color.green()}, {china_color.blue()}); font-weight: bold; font-size: 14px;")
        china_icon.setFixedWidth(20)
        china_label = QLabel("CHINA:")
        china_label.setStyleSheet("color: black; font-weight: normal;")
        china_label.setMinimumWidth(80)
        china_value_label = QLabel("-")
        china_value_label.setStyleSheet("background-color: #f9f9f9; border: 1px solid #ddd; padding: 2px; height: 22px;")
        
        # Add CHINA field to inspection_fields
        self.inspection_fields['CHINA'] = china_value_label
        
        # Add count label for CHINA field
        china_count_label = QLabel("0")
        china_count_label.setFixedWidth(50)
        china_count_label.setAlignment(Qt.AlignCenter)
        china_count_label.setStyleSheet("border: 1px solid gray; padding: 2px;")
        self.inspection_count_labels['CHINA'] = china_count_label
        
        inspection_data_layout.addWidget(china_icon, china_row, 0)
        inspection_data_layout.addWidget(china_label, china_row, 1)
        inspection_data_layout.addWidget(china_value_label, china_row, 2)
        inspection_data_layout.addWidget(china_count_label, china_row, 3)
        
        # 9. SEARCH field (last)
        search_row = len(field_names) + 4
        search_icon = QLabel("■")
        search_icon.setStyleSheet("color: rgb(255, 100, 0); font-weight: bold; font-size: 14px;")  # Orange
        search_icon.setFixedWidth(20)
        search_label = QLabel("SEARCH:")
        search_label.setStyleSheet("color: black; font-weight: normal;")
        search_label.setMinimumWidth(80)
        
        # Create editable input field for SEARCH
        self.search_input = QLineEdit()
        self.search_input.setPlaceholderText("Enter text to search...")
        self.search_input.setStyleSheet("height: 22px; padding: 2px;")
        self.search_input.textChanged.connect(self.on_search_text_changed)
        
        # Add count label for SEARCH
        search_count_label = QLabel("0")
        search_count_label.setFixedWidth(50)  # Increased width for "detected/reference" format
        search_count_label.setAlignment(Qt.AlignCenter)
        search_count_label.setStyleSheet("border: 1px solid gray; padding: 2px;")
        self.inspection_count_labels['SEARCH'] = search_count_label
        
        inspection_data_layout.addWidget(search_icon, search_row, 0)
        inspection_data_layout.addWidget(search_label, search_row, 1)
        inspection_data_layout.addWidget(self.search_input, search_row, 2)
        inspection_data_layout.addWidget(search_count_label, search_row, 3)
        
        # Complete field layout
        
        inspection_layout.addLayout(inspection_data_layout)
        inspection_frame.setLayout(inspection_layout)
        layout.addWidget(inspection_frame)
        
        # Initialize inspection data storage
        self.inspection_data = []
        
        # Initialize search inputs as empty dict for compatibility
        self.search_inputs = {}
        
        # Initialize count labels as empty dict for compatibility
        self.count_labels = {}
        
        # Terminal Display Area - expanded to fill remaining space
        ocr_text_label = QLabel("Terminal:")
        ocr_text_label.setFont(QFont("Arial", 10, QFont.Bold))
        layout.addWidget(ocr_text_label)
        
        self.ocr_text_display = QTextEdit()
        self.ocr_text_display.setReadOnly(True)
        self.ocr_text_display.setStyleSheet("background-color: #f9f9f9; font-family: monospace; font-size: 10px; margin: 0px; padding: 5px;")
        layout.addWidget(self.ocr_text_display)
        
        # Add a horizontal line below the OCR text display
        line = QFrame()
        line.setFrameShape(QFrame.HLine)
        line.setFrameShadow(QFrame.Sunken)
        layout.addWidget(line)
        
        # Save controls at bottom
        save_layout = QHBoxLayout()
        
        # Save path on left
        self.save_path_btn = QPushButton("저장경로")
        self.save_path_label = QLabel(self.save_path)
        self.save_path_label.setStyleSheet("color: gray; font-size: 8pt;")
        save_layout.addWidget(self.save_path_btn)
        save_layout.addWidget(self.save_path_label)
        
        # Save button on right
        self.save_image_btn = QPushButton("라벨 이미지 저장하기")
        save_layout.addWidget(self.save_image_btn)
        
        layout.addLayout(save_layout)
        
        # Save status below buttons
        self.save_status_label = QLabel("")
        self.save_status_label.setStyleSheet("color: green; font-size: 8pt;")
        layout.addWidget(self.save_status_label)
        
        # New button at the bottom
        self.new_btn = QPushButton("신규")
        layout.addWidget(self.new_btn)
        
        left_widget.setLayout(layout)
        return left_widget
    
    def on_search_text_changed(self, text):
        """Handle real-time search text changes"""
        if self.ocr_results:
            self.update_inspection_highlighting()
        
    def create_right_panel(self):
        right_widget = QFrame()
        right_widget.setFrameStyle(QFrame.StyledPanel)
        
        layout = QVBoxLayout()
        
        # Image display area with zoom and pan functionality
        self.image_scroll = ZoomableScrollArea()
        self.image_scroll.setWidgetResizable(False)  # Don't auto-resize to enable scrollbars
        self.image_scroll.setAlignment(Qt.AlignCenter)
        
        self.image_label = DraggableLabel("Select LIVE CAM or PDF VIEW to start")
        self.image_label.setAlignment(Qt.AlignCenter)
        self.image_label.setStyleSheet("border: 2px dashed #ccc; color: #999; font-size: 14pt;")
        self.image_label.setMinimumSize(1200, 800)
        
        # Mouse event functionality removed
        
        # Track field filling order
        self.field_fill_order = ['LOT', 'REF', 'PN', 'MFG DATE', 'EXP DATE']
        self.current_field_index = 0
        
        
        self.image_scroll.setWidget(self.image_label)
        layout.addWidget(self.image_scroll)
        
        # PDF Navigation Controls (initially hidden)
        self.pdf_nav_layout = QHBoxLayout()
        
        # Navigation buttons
        self.first_page_btn = QPushButton("첫 페이지")
        self.prev_page_btn = QPushButton("이전 페이지")
        self.next_page_btn = QPushButton("다음 페이지")
        self.last_page_btn = QPushButton("끝 페이지")
        
        # Style navigation buttons with different colors
        # Sky blue for first/last page buttons
        sky_blue_style = """
            QPushButton {
                background-color: #87CEEB;
                color: white;
                border: none;
                border-radius: 4px;
                padding: 5px 10px;
                font-weight: bold;
                min-width: 80px;
            }
            QPushButton:hover {
                background-color: #6BB6E6;
            }
            QPushButton:disabled {
                background-color: #cccccc;
                color: #666666;
            }
        """
        
        # Green for previous/next page buttons
        green_style = """
            QPushButton {
                background-color: #4CAF50;
                color: white;
                border: none;
                border-radius: 4px;
                padding: 5px 10px;
                font-weight: bold;
                min-width: 80px;
            }
            QPushButton:hover {
                background-color: #45a049;
            }
            QPushButton:disabled {
                background-color: #cccccc;
                color: #666666;
            }
        """
        
        self.first_page_btn.setStyleSheet(sky_blue_style)
        self.prev_page_btn.setStyleSheet(green_style)
        self.next_page_btn.setStyleSheet(green_style)
        self.last_page_btn.setStyleSheet(sky_blue_style)
        
        # Page info label
        self.page_info_label = QLabel("1 Page / 1 Page")
        self.page_info_label.setStyleSheet("font-weight: bold; color: #333; margin: 0 10px;")
        
        # Add to layout
        self.pdf_nav_layout.addWidget(self.first_page_btn)
        self.pdf_nav_layout.addWidget(self.prev_page_btn)
        self.pdf_nav_layout.addWidget(self.next_page_btn)
        self.pdf_nav_layout.addWidget(self.last_page_btn)
        self.pdf_nav_layout.addWidget(self.page_info_label)
        self.pdf_nav_layout.addStretch()
        
        # Hide PDF navigation initially
        self.first_page_btn.setVisible(False)
        self.prev_page_btn.setVisible(False)
        self.next_page_btn.setVisible(False)
        self.last_page_btn.setVisible(False)
        self.page_info_label.setVisible(False)
        
        layout.addLayout(self.pdf_nav_layout)
        
        # Zoom controls (initially hidden)
        zoom_layout = QHBoxLayout()
        self.zoom_slider = QSlider(Qt.Horizontal)
        self.zoom_slider.setRange(10, 500)
        self.zoom_slider.setValue(100)
        self.zoom_slider.setVisible(False)
        
        self.zoom_label = QLabel("100%")
        self.zoom_label.setVisible(False)
        
        zoom_layout.addWidget(QLabel("Zoom:"))
        zoom_layout.addWidget(self.zoom_slider)
        zoom_layout.addWidget(self.zoom_label)
        zoom_layout.addStretch()
        
        layout.addLayout(zoom_layout)
        
        # Progress bar (initially hidden)
        self.progress_bar = QProgressBar()
        self.progress_bar.setVisible(False)
        layout.addWidget(self.progress_bar)
        
        right_widget.setLayout(layout)
        # Connect signals
        self.camera_handler.frame_ready.connect(self.update_camera_frame)
        
        
        return right_widget
        
    def setup_connections(self):
        # Mode buttons
        self.live_cam_btn.clicked.connect(self.toggle_live_cam)
        self.pdf_view_btn.clicked.connect(self.toggle_pdf_view)
        
        # Control buttons
        self.cam_set_btn.clicked.connect(self.show_camera_settings)
        self.pdf_load_btn.clicked.connect(self.load_pdf)
        self.capture_ocr_btn.clicked.connect(self.capture_and_ocr)
        self.refresh_btn.clicked.connect(self.refresh_camera)
        
        
        # Save buttons
        self.save_path_btn.clicked.connect(self.select_save_path)
        self.save_image_btn.clicked.connect(self.toggle_auto_save)
        
        # New button
        self.new_btn.clicked.connect(self.reset_application)
        
        # Search inputs removed - no connections needed
            
        # Zoom slider
        self.zoom_slider.valueChanged.connect(self.update_zoom)
        
        # Connect zoomable scroll area signals
        self.image_scroll.zoom_changed.connect(self.on_mouse_wheel_zoom)
        
        # Enable dragging on image scroll area
        self.image_scroll.setDragMode(True)
        
        # OCR engine signals
        self.aws_textract_engine.ocr_completed.connect(self.on_ocr_completed)
        self.aws_textract_engine.progress_updated.connect(self.update_progress)
        
        
        # PDF navigation buttons
        self.first_page_btn.clicked.connect(self.go_to_first_page)
        self.prev_page_btn.clicked.connect(self.go_to_previous_page)
        self.next_page_btn.clicked.connect(self.go_to_next_page_with_auto_save)
        self.last_page_btn.clicked.connect(self.go_to_last_page)
        
        # PDF handler signals
        self.pdf_handler.page_changed.connect(self.on_pdf_page_changed)
    
    def setup_keyboard_shortcuts(self):
        """Setup keyboard shortcuts for navigation"""
        from PyQt5.QtWidgets import QShortcut
        from PyQt5.QtGui import QKeySequence
        from PyQt5.QtCore import Qt
        
        # Right arrow key for next page
        next_shortcut = QShortcut(QKeySequence(Qt.Key_Right), self)
        next_shortcut.activated.connect(self.go_to_next_page_with_auto_save)
        
        # Left arrow key for previous page
        prev_shortcut = QShortcut(QKeySequence(Qt.Key_Left), self)
        prev_shortcut.activated.connect(self.go_to_previous_page)
    
    def update_china_field_visibility(self):
        """Update CHINA field visibility based on current standard"""
        if hasattr(self, 'inspection_fields') and 'CHINA' in self.inspection_fields:
            # Show/hide based on current standard
            is_china_standard = self.current_standard == '중국'
            
            # Simply show/hide the CHINA field and its count label
            self.inspection_fields['CHINA'].setVisible(is_china_standard)
            
            if hasattr(self, 'inspection_count_labels') and 'CHINA' in self.inspection_count_labels:
                self.inspection_count_labels['CHINA'].setVisible(is_china_standard)
            
            # Find and hide/show the CHINA label and icon by searching through the parent widget
            parent_widget = self.inspection_fields['CHINA'].parent()
            if parent_widget:
                # Find all QLabel widgets in the parent and check their text
                for child in parent_widget.findChildren(QLabel):
                    if child.text() == 'CHINA:' or (child.text() == '■' and child.styleSheet().find('255, 165, 0') != -1):
                        child.setVisible(is_china_standard)
    
    def update_china_field_value(self):
        """Update CHINA field value based on REF field for 중국 standard"""
        if self.current_standard != '중국' or not hasattr(self, 'inspection_fields'):
            return
            
        ref_value = self.inspection_fields.get('REF', QLabel()).text().strip()
        if not ref_value or ref_value == '-':
            return
            
        # Check if REF starts with any of the mapping keys
        china_value = '-'
        for ref_prefix, china_code in self.china_ref_mapping.items():
            if ref_value.startswith(ref_prefix):
                china_value = china_code
                break
                
        # Update CHINA field
        if 'CHINA' in self.inspection_fields:
            self.inspection_fields['CHINA'].setText(china_value)
            
        # Update search terms for highlighting
        if china_value != '-':
            self.search_terms['CHINA'] = china_value
        elif 'CHINA' in self.search_terms:
            del self.search_terms['CHINA']
            
        # Update highlighting
        self.update_inspection_highlighting()
        
        # Update count for CHINA field
        if self.current_standard == '중국' and china_value != '-':
            self.update_inspection_highlighting()
        
    def toggle_live_cam(self):
        if self.live_cam_btn.isChecked():
            self.pdf_view_btn.setChecked(False)
            self.current_mode = 'camera'
            # Keep AWS TEXTRACT for LIVE CAM mode
            self.capture_ocr_btn.setVisible(True)  # Show CAPTURE+OCR button
            self.refresh_btn.setVisible(True)  # Show REFRESH button
            self.start_camera()
        else:
            self.stop_camera()
            self.capture_ocr_btn.setVisible(False)  # Hide CAPTURE+OCR button
            self.refresh_btn.setVisible(False)  # Hide REFRESH button
            self.current_mode = None
            
    def toggle_pdf_view(self):
        if self.pdf_view_btn.isChecked():
            self.live_cam_btn.setChecked(False)
            self.current_mode = 'pdf'
            if hasattr(self, 'current_pdf_path'):
                self.display_pdf()
        else:
            self.current_mode = None
            
    def start_camera(self):
        # Get available cameras and prioritize USB cameras
        available_cameras = self.camera_handler.get_available_cameras()
        if available_cameras:
            # Set to first available camera (USB prioritized)
            self.camera_handler.set_camera(available_cameras[0])
        
        if self.camera_handler.start_camera():
            self.camera_timer = QTimer()
            self.camera_timer.timeout.connect(self.update_camera_frame)
            self.camera_timer.start(30)  # 30ms = ~33 FPS
        else:
            QMessageBox.warning(self, "Camera Error", "Failed to start camera")
            self.live_cam_btn.setChecked(False)
            self.current_mode = None
            
    def stop_camera(self):
        if hasattr(self, 'camera_timer'):
            self.camera_timer.stop()
        self.camera_handler.stop_camera()
        
    def update_camera_frame(self):
        if not self.is_camera_frozen:
            frame = self.camera_handler.get_frame()
            if frame is not None:
                self.current_image = frame
                self.display_image_with_overlays(frame)
        else:
            # Display frozen image with zoom capability
            if self.frozen_image is not None:
                self.display_image_with_overlays(self.frozen_image)
    def show_camera_settings(self):
        cameras = self.camera_handler.get_available_cameras()
        if not cameras:
            QMessageBox.information(self, "No Cameras", "No cameras found")
            return
            
        # Toggle to next available camera
        current_camera = self.camera_handler.current_camera_index
        try:
            current_index = cameras.index(current_camera)
            next_index = (current_index + 1) % len(cameras)
            next_camera = cameras[next_index]
        except ValueError:
            # Current camera not in list, use first available
            next_camera = cameras[0]
        
        if self.current_mode == 'camera':
            self.stop_camera()
            self.camera_handler.set_camera(next_camera)
            self.start_camera()
        else:
            self.camera_handler.set_camera(next_camera)
            
        print(f"Switched to camera {next_camera}")
    
    def capture_and_ocr(self):
        """Capture current camera frame, freeze it, and run OCR"""
        if self.current_mode != 'camera' or self.current_image is None:
            QMessageBox.warning(self, "Camera Error", "Camera is not active or no image available")
            return
            
        try:
            # Freeze the current frame
            self.frozen_image = self.current_image.copy()
            self.is_camera_frozen = True
            
            # Enable zoom functionality for frozen image
            self.image_scroll.setDragMode(True)
            
            # Save captured image
            import datetime
            timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
            filename = f"captured_{timestamp}.jpg"
            filepath = os.path.join(self.save_path, filename)
            
            # Convert frame to BGR for saving
            if len(self.current_image.shape) == 3:
                bgr_image = cv2.cvtColor(self.current_image, cv2.COLOR_RGB2BGR)
            else:
                bgr_image = self.current_image
                
            cv2.imwrite(filepath, bgr_image)
            print(f"Captured image saved: {filepath}")
            
            # Run OCR on captured image
            self.run_current_ocr_engine(self.current_image)
            
            # Show message
            QMessageBox.information(self, "Capture Complete", f"Image captured and OCR processing started.\nSaved: {filename}")
            
        except Exception as e:
            print(f"Error capturing image: {e}")
            QMessageBox.warning(self, "Capture Error", f"Failed to capture image: {str(e)}")
        finally:
            # Keep camera timer running for live updates when not frozen
            pass
    
    def refresh_camera(self):
        """Unfreeze camera and return to live view"""
        if self.current_mode == 'camera':
            self.is_camera_frozen = False
            self.frozen_image = None
            print("Camera refreshed - returning to live view")
    
    def select_save_path(self):
        """Open dialog to select save path for images"""
        from PyQt5.QtWidgets import QFileDialog
        
        folder_path = QFileDialog.getExistingDirectory(
            self, 
            "저장 경로 선택", 
            self.save_path,
            QFileDialog.ShowDirsOnly | QFileDialog.DontResolveSymlinks
        )
        
        if folder_path:
            self.save_path = folder_path
            self.save_path_label.setText(self.save_path)
            print(f"Save path updated to: {self.save_path}")
    
    def toggle_auto_save(self):
        """Toggle auto save mode on/off"""
        self.auto_save_enabled = not self.auto_save_enabled
        
        if self.auto_save_enabled:
            self.save_image_btn.setText("자동저장 ON")
            self.save_image_btn.setStyleSheet("background-color: #4CAF50; color: white; font-weight: bold;")
            print("Auto save mode enabled")
            # Save current page when enabling auto save
            self.save_image_with_overlays()
        else:
            self.save_image_btn.setText("라벨 이미지 저장하기")
            self.save_image_btn.setStyleSheet("")
            print("Auto save mode disabled")
            # Save current page when disabling auto save (for last page)
            if hasattr(self, 'pdf_handler') and hasattr(self.pdf_handler, 'current_page') and hasattr(self.pdf_handler, 'total_pages'):
                if self.pdf_handler.current_page == self.pdf_handler.total_pages:
                    self.save_image_with_overlays()
    
    def auto_save_if_enabled(self):
        """Auto save image if auto save is enabled and conditions are met"""
        if not self.auto_save_enabled:
            return
            
        # Check if inspection standard is selected
        if not self.current_standard:
            print("Auto save skipped: No inspection standard selected")
            return
            
        # Check if LOT information is available
        lot_value = self.search_terms.get('LOT', '').strip()
        if not lot_value:
            print("Auto save skipped: No LOT information available")
            return
            
        # All conditions met, save the image
        print("Auto saving image...")
        self.save_image_with_overlays()
    
    def go_to_next_page_with_auto_save(self):
        """Go to next page and auto save if enabled"""
        # Auto save current page if enabled and not on first page
        if self.auto_save_enabled and hasattr(self, 'pdf_handler'):
            if hasattr(self.pdf_handler, 'current_page') and self.pdf_handler.current_page > 1:
                self.auto_save_if_enabled()
        
        # Then go to next page
        self.go_to_next_page()
                
    def load_pdf(self):
        file_path, _ = QFileDialog.getOpenFileName(
            self, "Select PDF File", "", "PDF Files (*.pdf)"
        )
        
        if file_path:
            if self.pdf_handler.load_pdf(file_path):
                self.current_pdf_path = file_path
                filename = os.path.basename(file_path)
                self.pdf_name_label.setText(filename)
                self.pdf_name_label.setStyleSheet("color: black; font-weight: bold;")
                
                # Auto-switch to PDF mode and display
                self.pdf_view_btn.setChecked(True)
                self.live_cam_btn.setChecked(False)
                self.current_mode = 'pdf'
                self.display_pdf()
                
                # Show PDF navigation controls
                self.show_pdf_navigation()
                
                # Auto-start OCR processing with skew correction
                image = self.pdf_handler.get_current_page_image()
                if image is not None:
                    # Apply automatic skew correction before OCR
                    corrected_image = self.auto_skew_correction(image)
                    self.current_image = corrected_image
                    self.display_image_with_overlays(corrected_image)
                    self.run_current_ocr_engine(corrected_image)
            else:
                QMessageBox.warning(self, "PDF Error", "Could not load PDF file")
                
    def display_pdf(self):
        if hasattr(self, 'current_pdf_path'):
            image = self.pdf_handler.get_current_page_image()
            if image is not None:
                self.current_image = image
                self.display_image_with_overlays(image)
                self.zoom_slider.setVisible(True)
                self.zoom_label.setVisible(True)
                
                # Apply automatic skew correction before OCR
                corrected_image = self.auto_skew_correction(image)
                self.current_image = corrected_image
                self.display_image_with_overlays(corrected_image)
                
                # Auto-start OCR processing
                self.run_current_ocr_engine(corrected_image)
                    
    def display_image_with_overlays(self, image):
        """Display image with OCR and barcode overlays"""
        if image is None:
            return
            
        # Convert image to QPixmap
        from PyQt5.QtGui import QImage
        height, width = image.shape[:2]
        if len(image.shape) == 3:
            bytes_per_line = 3 * width
            q_image = QImage(image.data, width, height, bytes_per_line, QImage.Format_RGB888)
        else:
            bytes_per_line = width
            q_image = QImage(image.data, width, height, bytes_per_line, QImage.Format_Grayscale8)
            
        pixmap = QPixmap.fromImage(q_image)
        
        # Auto-fit PDF to window on first load, then apply zoom
        if self.current_mode == 'pdf':
            # Get available display area size
            scroll_area_size = self.image_scroll.size()
            available_width = scroll_area_size.width() - 20  # Account for scrollbars
            available_height = scroll_area_size.height() - 20
            
            # Calculate fit-to-window scale factor
            scale_x = available_width / pixmap.width()
            scale_y = available_height / pixmap.height()
            fit_scale = min(scale_x, scale_y, 1.0)  # Don't upscale beyond original size
            
            # Apply zoom factor on top of fit scale
            zoom_factor = self.zoom_slider.value() / 100.0
            final_scale = fit_scale * zoom_factor
            
            pixmap = pixmap.scaled(
                int(pixmap.width() * final_scale),
                int(pixmap.height() * final_scale),
                Qt.KeepAspectRatio,
                Qt.SmoothTransformation
            )
        
        # Create painter for overlays
        painter = QPainter(pixmap)
        
        # Calculate scale factor for coordinates
        scale_factor = 1.0
        if self.current_mode == 'pdf':
            scroll_area_size = self.image_scroll.size()
            available_width = scroll_area_size.width() - 20
            available_height = scroll_area_size.height() - 20
            scale_x = available_width / width
            scale_y = available_height / height
            fit_scale = min(scale_x, scale_y, 1.0)
            zoom_factor = self.zoom_slider.value() / 100.0
            scale_factor = fit_scale * zoom_factor
        
        # Skip light green highlighting for all OCR text - only highlight matched search terms
        
        # Draw search term highlights on top with numbering (border only)
        if hasattr(self, 'ocr_results') and self.ocr_results:
            colors = {
                'LOT': QColor(200, 0, 0),        # Darker red
                'REF': QColor(0, 100, 200),      # Darker sky blue  
                'PN': QColor(0, 150, 0),         # Darker green
                'MFG DATE': QColor(200, 200, 0), # Darker yellow
                'EXP DATE': QColor(150, 0, 150), # Darker purple
                'SEARCH': QColor(255, 100, 0),   # Orange for search
                'PRODUCTS': QColor(100, 100, 100), # Gray for products
                'GTIN': QColor(0, 200, 200)      # Cyan for GTIN
            }
            
            for term, search_text in self.search_terms.items():
                if search_text.strip():  # Only highlight if there's search text
                    # Set color based on field type
                    if term == 'GTIN':
                        color = QColor(0, 255, 255)  # Bright cyan for GTIN
                    else:
                        # Use original colors for other fields
                        field_colors = {
                            'LOT': QColor(200, 0, 0),        # Red
                            'REF': QColor(0, 100, 200),      # Blue  
                            'PN': QColor(0, 150, 0),         # Green
                            'MFG DATE': QColor(200, 200, 0), # Yellow
                            'EXP DATE': QColor(150, 0, 150), # Purple
                            'SEARCH': QColor(255, 100, 0),   # Orange
                            'PRODUCTS': QColor(100, 100, 100), # Gray
                            'GTIN': QColor(0, 255, 255),     # Cyan
                            'CHINA': QColor(255, 165, 0)     # Orange (same as icon)
                        }
                        color = field_colors.get(term, QColor(128, 128, 128))
                    
                    painter.setPen(QPen(color, 2))  # Set to 2 points for uniform thickness
                    
                    matching_items = []
                    
                    # Special handling for GTIN - count only (01) pattern matches
                    if term == 'GTIN':
                        import re
                        for ocr_item in self.ocr_results:
                            ocr_text = ocr_item['text'].strip()
                            if '(01)' in ocr_text:
                                match_01 = re.search(r'\(01\)(\d{14})', ocr_text)
                                if match_01:
                                    gtin_from_ocr = match_01.group(1)
                                    if gtin_from_ocr == search_text:
                                        matching_items.append(ocr_item)
                                        print(f"DEBUG: GTIN match found in '{ocr_text}' -> {gtin_from_ocr}")
                    else:
                        # Regular text matching for other fields (including CHINA)
                        for ocr_item in self.ocr_results:
                            ocr_text = ocr_item['text'].strip()
                            if search_text.lower() in ocr_text.lower():
                                matching_items.append(ocr_item)
                                if term == 'CHINA':
                                    print(f"DEBUG: CHINA match found in '{ocr_text}' -> {search_text}")
                    
                    # Draw rectangles for matching items
                    for i, ocr_item in enumerate(matching_items, 1):
                        x, y, w, h = ocr_item['bbox']
                        
                        # Apply scale factor to coordinates
                        x = int(x * scale_factor)
                        y = int(y * scale_factor)
                        w = int(w * scale_factor)
                        h = int(h * scale_factor)
                        
                        # Expand rectangle size to avoid covering text
                        padding = 3
                        x -= padding
                        y -= padding
                        w += padding * 2
                        h += padding * 2
                        
                        # Draw highlight rectangle (border only)
                        painter.drawRect(x, y, w, h)
        
        painter.end()
        
        # Update image label with proper sizing for scrollbars
        self.image_label.setPixmap(pixmap)
        self.image_label.setScaledContents(False)
        self.image_label.resize(pixmap.size())  # Set exact size to enable scrollbars
        
    def update_ocr_text_display(self):
        """Update the OCR text display area with all detected text - same format as command logs"""
        if hasattr(self, 'ocr_results') and self.ocr_results:
            text_lines = []
            for item in self.ocr_results:
                confidence = item['confidence']
                text = item['text']
                # Match the command log format exactly
                text_lines.append(f"  Found text: '{text}' (confidence: {confidence}%)")
            
            display_text = "\n".join(text_lines)
            self.ocr_text_display.setPlainText(display_text)
            
            # Auto-scroll to bottom to show latest logs
            scrollbar = self.ocr_text_display.verticalScrollBar()
            scrollbar.setValue(scrollbar.maximum())
        else:
            self.ocr_text_display.setPlainText("No OCR results available")
    

    
    def apply_grayscale(self):
        if self.current_image is not None:
            if len(self.current_image.shape) == 3:
                gray_image = cv2.cvtColor(self.current_image, cv2.COLOR_BGR2GRAY)
                self.current_image = cv2.cvtColor(gray_image, cv2.COLOR_GRAY2BGR)
                self.display_image_with_overlays(self.current_image)
                
    def apply_skew_correction(self):
        if self.current_image is not None:
            try:
                print("Starting skew correction...")
                
                # Convert to grayscale
                if len(self.current_image.shape) == 3:
                    gray = cv2.cvtColor(self.current_image, cv2.COLOR_RGB2GRAY)
                else:
                    gray = self.current_image.copy()
                
                # Method 1: Hough Line Transform for text line detection
                edges = cv2.Canny(gray, 50, 150, apertureSize=3)
                lines = cv2.HoughLines(edges, 1, np.pi/180, threshold=100)
                
                angles = []
                if lines is not None:
                    for line in lines[:20]:  # Use top 20 lines
                        rho, theta = line[0]  # Extract from nested array
                        angle = theta * 180 / np.pi - 90
                        # Filter for horizontal-ish lines
                        if abs(angle) < 45:
                            angles.append(angle)
                
                # Method 2: Text contour analysis as fallback
                if not angles:
                    # Apply morphological operations to connect text
                    kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (20, 1))
                    dilated = cv2.dilate(gray, kernel, iterations=1)
                    
                    # Find contours of text lines
                    contours, _ = cv2.findContours(dilated, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
                    
                    for contour in contours:
                        if cv2.contourArea(contour) > 500:  # Filter small contours
                            rect = cv2.minAreaRect(contour)
                            angle = rect[2]
                            
                            # Normalize angle
                            if angle < -45:
                                angle = 90 + angle
                            elif angle > 45:
                                angle = angle - 90
                            
                            # Filter out specific excluded words (keep important label info like LOT, REF)
                            excluded_words = [
                                'Internal', 'Use Only', 'DIAMETER', 'TOTAL', 
                                'LENGTH', 'Lifetime', 'MANUFACTURER', 'DATE OF', 
                                'UDI', 'USABLE', 'HANAROSTENT', 'COLON', 'RECTUM', 'TTS', 'TIS', 'CCC', 
                                'NCN', 'CCN', 'NCC', 'NC', 'Fully', 'FULLEY', 'covered', 
                                'SPECIFICATION', '데이터매트릭스', '바코드', 'USE BY',
                                'MANUFACTURE', 'GUIDE', 'WIRE', 'onl', 'psl', 'days', 'within',
                                'Lasso', 'Stent', 'Delivery', 'Device', 'Use'  # 새로 추가된 제외 단어
                            ]
                            x, y, w, h = cv2.boundingRect(contour)
                            roi = gray[y:y+h, x:x+w]
                            # Skip text extraction for skew detection - use geometric analysis only
                            if abs(angle) < 45:  # Only consider reasonable angles
                                angles.append(angle)
                
                if angles:
                    # Calculate median angle for robustness
                    median_angle = np.median(angles)
                    print(f"Detected skew angle: {median_angle:.2f} degrees")
                    
                    # Only apply correction if angle is significant (> 0.5 degrees)
                    if abs(median_angle) > 0.5:
                        (h, w) = self.current_image.shape[:2]
                        center = (w // 2, h // 2)
                        
                        # Create rotation matrix
                        M = cv2.getRotationMatrix2D(center, median_angle, 1.0)
                        
                        # Calculate new image dimensions to avoid cropping
                        cos = np.abs(M[0, 0])
                        sin = np.abs(M[0, 1])
                        new_w = int((h * sin) + (w * cos))
                        new_h = int((h * cos) + (w * sin))
                        
                        # Adjust translation to center the rotated image
                        M[0, 2] += (new_w / 2) - center[0]
                        M[1, 2] += (new_h / 2) - center[1]
                        
                        # Apply rotation with high-quality interpolation
                        self.current_image = cv2.warpAffine(self.current_image, M, (new_w, new_h), 
                                                          flags=cv2.INTER_CUBIC, 
                                                          borderMode=cv2.BORDER_REPLICATE)
                        
                        print(f"Applied skew correction: {median_angle:.2f} degrees")
                        self.display_image_with_overlays(self.current_image)
                        
                        # Re-run OCR after skew correction
                        self.run_current_ocr_engine(self.current_image)
                    else:
                        print(f"Skew angle too small ({median_angle:.2f}°), no correction needed")
                        QMessageBox.information(self, "Skew Correction", f"Image skew is minimal ({median_angle:.2f}°), no correction needed.")
                else:
                    print("No text lines detected for skew correction")
                    QMessageBox.information(self, "Skew Correction", "No clear text lines detected for skew analysis.")
                            
            except Exception as e:
                print(f"Skew correction error: {e}")
                QMessageBox.warning(self, "Skew Correction Error", f"Could not apply skew correction: {e}")
    
    def auto_skew_correction(self, image):
        """Automatically apply skew correction to image without user interaction"""
        try:
            print("Applying automatic skew correction...")
            
            # Convert to grayscale
            if len(image.shape) == 3:
                gray = cv2.cvtColor(image, cv2.COLOR_RGB2GRAY)
            else:
                gray = image.copy()
            
            # Method 1: Hough Line Transform for text line detection
            edges = cv2.Canny(gray, 50, 150, apertureSize=3)
            lines = cv2.HoughLines(edges, 1, np.pi/180, threshold=100)
            
            angles = []
            if lines is not None:
                for line in lines[:20]:  # Use top 20 lines
                    rho, theta = line[0]  # Extract from nested array
                    angle = theta * 180 / np.pi - 90
                    # Filter for horizontal-ish lines
                    if abs(angle) < 45:
                        angles.append(angle)
            
            # Method 2: Text contour analysis as fallback
            if not angles:
                # Apply morphological operations to connect text
                kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (20, 1))
                dilated = cv2.dilate(gray, kernel, iterations=1)
                
                # Find contours of text lines
                contours, _ = cv2.findContours(dilated, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
                
                for contour in contours:
                    if cv2.contourArea(contour) > 500:  # Filter small contours
                        rect = cv2.minAreaRect(contour)
                        angle = rect[2]
                        
                        # Normalize angle
                        if angle < -45:
                            angle = 90 + angle
                        elif angle > 45:
                            angle = angle - 90
                        
                        if abs(angle) < 45:  # Only consider reasonable angles
                            angles.append(angle)
            
            if angles:
                # Calculate median angle for robustness
                median_angle = np.median(angles)
                print(f"Auto-detected skew angle: {median_angle:.2f} degrees")
                
                # Only apply correction if angle is significant (> 0.5 degrees)
                if abs(median_angle) > 0.5:
                    (h, w) = image.shape[:2]
                    center = (w // 2, h // 2)
                    
                    # Create rotation matrix
                    M = cv2.getRotationMatrix2D(center, median_angle, 1.0)
                    
                    # Calculate new image dimensions to avoid cropping
                    cos = np.abs(M[0, 0])
                    sin = np.abs(M[0, 1])
                    new_w = int((h * sin) + (w * cos))
                    new_h = int((h * cos) + (w * sin))
                    
                    # Adjust translation to center the rotated image
                    M[0, 2] += (new_w / 2) - center[0]
                    M[1, 2] += (new_h / 2) - center[1]
                    
                    # Apply rotation with high-quality interpolation
                    corrected_image = cv2.warpAffine(image, M, (new_w, new_h), 
                                                   flags=cv2.INTER_CUBIC, 
                                                   borderMode=cv2.BORDER_REPLICATE)
                    
                    print(f"Auto-applied skew correction: {median_angle:.2f} degrees")
                    return corrected_image
                else:
                    print(f"Skew angle too small ({median_angle:.2f}°), no correction needed")
                    return image
            else:
                print("No text lines detected for auto skew correction")
                return image
                        
        except Exception as e:
            print(f"Auto skew correction error: {e}")
            return image  # Return original image if correction fails
    
        
        # Re-run OCR if image is loaded
        if self.current_image is not None:
            self.run_current_ocr_engine(self.current_image)
    
    def select_aws_textract_engine(self):
        """Switch to AWS TEXTRACT OCR engine"""
        if not self.aws_textract_engine.is_available():
            QMessageBox.warning(self, "AWS TEXTRACT Error", 
                              "AWS TEXTRACT is not available. Please configure AWS credentials and ensure proper IAM permissions.")
            return
            
        self.current_ocr_engine = 'aws_textract'
        self.aws_textract_btn.setChecked(True)
        print("Switched to AWS TEXTRACT OCR engine")
        
        # Re-run OCR if image is loaded
        if self.current_image is not None:
            self.run_current_ocr_engine(self.current_image)
    
    def open_ocr_settings(self):
        """Open OCR settings dialog"""
        dialog = OCRSettingsDialog(self)
        if dialog.exec_() == QDialog.Accepted:
            # Apply new settings
            settings = dialog.get_settings()
            self.apply_ocr_settings(settings)
    
    def apply_ocr_settings(self, settings):
        """Apply OCR settings to engines"""
        
        # Apply to AWS TEXTRACT engine
        if hasattr(self.aws_textract_engine, 'apply_settings'):
            self.aws_textract_engine.apply_settings(settings)
        
        # Re-run OCR if image is loaded
        if self.current_image is not None:
            self.run_current_ocr_engine(self.current_image)
    
    def run_current_ocr_engine(self, image):
        """Run OCR with currently selected engine"""
        if self.current_ocr_engine == 'aws_textract':
            if not self.aws_textract_engine.is_processing():
                # Set image dimensions for coordinate scaling
                self.aws_textract_engine._current_image_width = image.shape[1]
                self.aws_textract_engine._current_image_height = image.shape[0]
                self.aws_textract_engine.process_image(image)
                        
    def update_search_term_realtime(self, term, text):
        """Update search term and trigger real-time highlighting"""
        self.search_terms[term] = text
        print(f"Searching for '{text}' in category '{term}'")
        
        # Update count immediately if we have OCR results
        if self.ocr_results:
            # Count matches for current search term across all OCR text
            matches = []
            if text.strip():  # Only search if there's actual text
                for ocr_item in self.ocr_results:
                    if text.lower() in ocr_item['text'].lower():
                        matches.append(ocr_item)
                        print(f"  Found match: '{ocr_item['text']}'")
            count = len(matches)
            self.count_labels[term].setText(str(count))
            print(f"  Total matches: {count}")
        else:
            self.count_labels[term].setText("0")
            print("  No OCR results available")
        
        # Trigger real-time re-highlighting if we have OCR results and current image
        # Removed duplicate call - display_image_with_overlays is called elsewhere
            
    def update_search_term(self, term, text):
        """Legacy method - kept for compatibility"""
        self.update_search_term_realtime(term, text)
            
    def update_zoom(self, value):
        self.zoom_label.setText(f"{value}%")
        if self.current_mode == 'pdf' and self.current_image is not None:
            self.display_image_with_overlays(self.current_image)
            
    def on_mouse_wheel_zoom(self, zoom_percentage):
        """Handle mouse wheel zoom from ZoomableScrollArea"""
        self.zoom_slider.setValue(zoom_percentage)
        self.zoom_label.setText(f"{zoom_percentage}%")
        if self.current_mode == 'pdf' and self.current_image is not None:
            self.display_image_with_overlays(self.current_image)
    
    def save_image_with_overlays(self):
        """Save current image with all overlays including highlighting and count information"""
        if not hasattr(self, 'current_image') or self.current_image is None:
            QMessageBox.warning(self, "Warning", "No image to save")
            return
        
        try:
            # Create a copy of the current image with 50% size reduction
            if isinstance(self.current_image, np.ndarray):
                height, width, channel = self.current_image.shape
                bytes_per_line = 3 * width
                q_image = QImage(self.current_image.data, width, height, bytes_per_line, QImage.Format_RGB888).rgbSwapped()
                pixmap = QPixmap.fromImage(q_image)
            else:
                pixmap = QPixmap.fromImage(self.current_image)
            
            # Reduce image size by 50%
            new_width = int(pixmap.width() * 0.5)
            new_height = int(pixmap.height() * 0.5)
            pixmap = pixmap.scaled(new_width, new_height, Qt.KeepAspectRatio, Qt.SmoothTransformation)
            
            # Create painter to draw overlays
            painter = QPainter(pixmap)
            painter.setRenderHint(QPainter.Antialiasing)
            
            # Draw highlighting rectangles (same as display_image_with_overlays)
            if hasattr(self, 'ocr_results') and self.ocr_results:
                scale_factor = 0.5  # Adjust scale factor for 50% reduced image size
                
                # Define colors for each field
                colors = {
                    'LOT': QColor(200, 0, 0),
                    'REF': QColor(0, 100, 200),
                    'PN': QColor(0, 150, 0),
                    'MFG DATE': QColor(200, 200, 0),
                    'EXP DATE': QColor(150, 0, 150),
                    'SEARCH': QColor(255, 100, 0),
                    'PRODUCTS': QColor(100, 100, 100),
                    'GTIN': QColor(0, 255, 255)
                }
                
                # Draw highlighting for each search term
                for term, search_text in self.search_terms.items():
                    if search_text.strip():
                        color = colors.get(term, QColor(128, 128, 128))
                        painter.setPen(QPen(color, 2))
                        
                        matching_items = []
                        if term == 'GTIN':
                            # GTIN matching logic
                            import re
                            for ocr_item in self.ocr_results:
                                text = ocr_item['text']
                                gtin_pattern = r'\(01\)(\d{14})'
                                match = re.search(gtin_pattern, text)
                                if match and match.group(1) == search_text:
                                    matching_items.append(ocr_item)
                        else:
                            # Other fields matching logic
                            for ocr_item in self.ocr_results:
                                if search_text.lower() in ocr_item['text'].lower():
                                    matching_items.append(ocr_item)
                        
                        # Draw rectangles for matching items
                        for ocr_item in matching_items:
                            x, y, w, h = ocr_item['bbox']
                            x = int(x * scale_factor)
                            y = int(y * scale_factor)
                            w = int(w * scale_factor)
                            h = int(h * scale_factor)
                            padding = 3
                            x -= padding
                            y -= padding
                            w += padding * 2
                            h += padding * 2
                            painter.drawRect(x, y, w, h)
            
            # Draw count information overlay at top right (adjusted for 50% size)
            if hasattr(self, 'inspection_count_labels'):
                font = QFont("Arial", 20, QFont.Bold)  # Reduced font size for 50% image
                painter.setFont(font)
                
                # Position for overlay text (top right area, adjusted for smaller image)
                overlay_x = pixmap.width() - 225  # 50% of original 450
                overlay_y = 30  # 50% of original 60
                line_height = 25  # 50% of original 50
                
                # Calculate background height including label type and CHINA field
                field_count = 7 if self.current_standard == '중국' else 6
                bg_height = 60 + (field_count * line_height)  # Dynamic height based on fields
                
                # Background rectangle for better readability (adjusted for 50% size)
                bg_rect = QRect(overlay_x - 10, overlay_y - 20, 215, bg_height)
                painter.fillRect(bg_rect, QColor(255, 255, 255, 200))  # Semi-transparent white
                painter.setPen(QPen(QColor(0, 0, 0), 1))  # Thinner pen for smaller image
                painter.drawRect(bg_rect)
                
                # Draw label type at the top
                if self.current_standard:
                    painter.setPen(QPen(QColor(0, 0, 255), 2))  # Blue color for label type
                    painter.drawText(overlay_x, overlay_y, f"Label: {self.current_standard}")
                    overlay_y += line_height  # Move down for field counts
                
                # Draw count information for each field (include CHINA for 중국 standard)
                fields = ['LOT', 'PN', 'REF', 'MFG DATE', 'EXP DATE', 'GTIN']
                if self.current_standard == '중국':
                    fields.append('CHINA')
                for i, field in enumerate(fields):
                    if field in self.inspection_count_labels:
                        label = self.inspection_count_labels[field]
                        count_text = label.text()
                        
                        # Remove HTML tags for all fields
                        if '<span' in count_text:
                            import re
                            count_text = re.sub(r'<[^>]+>', '', count_text)
                        
                        # Set text color based on count comparison
                        if field in self.reference_counts.get(self.current_standard, {}):
                            reference_count = self.reference_counts[self.current_standard][field]
                            if '/' in count_text:
                                try:
                                    current_count = int(count_text.split(':')[1].split('/')[0].strip())
                                    if current_count == reference_count:
                                        painter.setPen(QPen(QColor(0, 128, 0), 1))  # Green for match
                                    else:
                                        painter.setPen(QPen(QColor(255, 0, 0), 1))  # Red for mismatch
                                except (ValueError, IndexError):
                                    painter.setPen(QPen(QColor(255, 0, 0), 1))  # Red for mismatch
                            else:
                                painter.setPen(QPen(QColor(255, 0, 0), 1))  # Red for mismatch
                        else:
                            painter.setPen(QPen(QColor(0, 0, 0)))
                        
                        painter.drawText(overlay_x, overlay_y + (i * line_height), count_text)
            
            painter.end()
            
            # Generate filename with LOT, REF, and date
            from datetime import datetime
            date_str = datetime.now().strftime("%Y%m%d")
            
            # Extract LOT and REF from search terms
            lot_value = self.search_terms.get('LOT', '').strip()
            ref_value = self.search_terms.get('REF', '').strip()
            
            # Use default values if not found
            if not lot_value:
                lot_value = "UNKNOWN"
            if not ref_value:
                ref_value = "UNKNOWN"
            
            # Generate sequential counter with 3 digits
            counter_str = f"{self.file_counter:03d}"
            
            # Check if all counts match reference counts for pass/check status
            status_suffix = self.get_inspection_status_suffix()
            
            # Create filename: ###_LOT_REF_YYYYMMDD_Status
            filename = f"{counter_str}_{lot_value}_{ref_value}_{date_str}{status_suffix}.jpg"
            full_path = os.path.join(self.save_path, filename)
            
            # Increment counter for next save
            self.file_counter += 1
            
            # Save the image with overlays as JPEG with 85% quality
            pixmap.save(full_path, "JPEG", 85)
            self.save_status_label.setText(f"Image saved: {filename}")
            self.save_status_label.setStyleSheet("color: green; font-size: 8pt;")
            print(f"Image saved with overlays: {full_path}")
            
        except Exception as e:
            self.save_status_label.setText(f"Save failed: {str(e)}")
            self.save_status_label.setStyleSheet("color: red; font-size: 8pt;")
            print(f"Error saving image: {str(e)}")
    
    def get_inspection_status_suffix(self):
        """Check if all field counts match reference counts and return appropriate suffix"""
        if not self.current_standard or not hasattr(self, 'inspection_count_labels'):
            return "_Check"
        
        reference_counts = self.reference_counts.get(self.current_standard, {})
        fields = ['LOT', 'PN', 'REF', 'MFG DATE', 'EXP DATE', 'GTIN']
        if self.current_standard == '중국':
            fields.append('CHINA')
        
        all_match = True
        for field in fields:
            if field in self.inspection_count_labels and field in reference_counts:
                label = self.inspection_count_labels[field]
                count_text = label.text()
                
                # Extract current count from label text (format: "field: current/reference")
                if '/' in count_text:
                    try:
                        # Remove HTML tags if present
                        if '<span' in count_text:
                            import re
                            count_text = re.sub(r'<[^>]+>', '', count_text)
                        
                        # Extract current count (before the '/')
                        current_count = int(count_text.split(':')[1].split('/')[0].strip())
                        reference_count = reference_counts[field]
                        
                        if current_count != reference_count:
                            all_match = False
                            break
                    except (ValueError, IndexError):
                        all_match = False
                        break
                else:
                    all_match = False
                    break
        
        return "_Passed" if all_match else "_Check"
    
    def draw_ocr_highlights(self, painter, pixmap_size, scale_factor):
        """Draw OCR highlights on the image"""
        if not hasattr(self, 'ocr_results') or not self.ocr_results:
            return
        
        field_colors = {
            'LOT': QColor(200, 0, 0),
            'REF': QColor(0, 100, 200),
            'PN': QColor(0, 150, 0),
            'MFG DATE': QColor(200, 200, 0),
            'EXP DATE': QColor(150, 0, 150),
            'SEARCH': QColor(255, 100, 0),
            'PRODUCTS': QColor(100, 100, 100),
            'GTIN': QColor(0, 255, 255),  # Bright cyan for GTIN
            'CHINA': QColor(255, 165, 0)  # Orange for CHINA
        }
        for term, search_text in self.search_terms.items():
            if search_text.strip():
                color = field_colors.get(term, QColor(128, 128, 128))
                painter.setPen(QPen(color, 2))  # 2 point thickness
                
                matching_items = []
                
                if term == 'GTIN':
                    import re
                    for ocr_item in self.ocr_results:
                        ocr_text = ocr_item['text'].strip()
                        if '(01)' in ocr_text:
                            match_01 = re.search(r'\(01\)(\d{14})', ocr_text)
                            if match_01 and match_01.group(1) == search_text:
                                matching_items.append(ocr_item)
                else:
                    for ocr_item in self.ocr_results:
                        ocr_text = ocr_item['text'].strip()
                        if ('(01)' not in ocr_text and
                            search_text.lower() in ocr_text.lower() and
                            ocr_text.upper() not in ['LOT', 'PN', 'REF', 'MFG DATE', 'EXP DATE', 'PRODUCTS'] and
                            not ocr_text.upper().endswith(':') and
                            len(ocr_text.strip()) > 2):
                            matching_items.append(ocr_item)
                
                # Draw rectangles for matching items
                for ocr_item in matching_items:
                    x, y, w, h = ocr_item['bbox']
                    x = int(x * scale_factor)
                    y = int(y * scale_factor)
                    w = int(w * scale_factor)
                    h = int(h * scale_factor)
                    
                    # Add padding
                    padding = 3
                    x -= padding
                    y -= padding
                    w += padding * 2
                    h += padding * 2
                    
                    painter.drawRect(x, y, w, h)
    
    def add_overlay_text(self, painter, image_width, image_height):
        """Add overlay text with field values and counts in simplified format"""
        # Set 20pt font as requested
        font = QFont("Arial", 20, QFont.Bold)
        painter.setFont(font)
        painter.setPen(QColor(255, 255, 255))  # White text
        
        # Calculate overlay dimensions for simplified content
        overlay_width = 400
        overlay_height = 200
        overlay_x = image_width - overlay_width - 15
        overlay_y = 15
        
        # Draw semi-transparent background
        painter.fillRect(overlay_x, overlay_y, overlay_width, overlay_height, QColor(0, 0, 0, 200))
        
        # Prepare simplified text lines with square brackets
        lines = []
        
        # Get LOT value and count
        selected_lot = self.lot_combo.currentText()
        if selected_lot and selected_lot != "Select LOT...":
            lot_count = self.inspection_count_labels.get('LOT', QLabel()).text()
            # Convert (x/y) format to [x/y] format
            if '/' in lot_count:
                lot_count = lot_count.replace('<span style="color: #006400;">', '').replace('</span>', '').replace('<span style="color: black;">', '').replace('</span>', '')
                lot_count = f"[{lot_count}]"
            lines.append(f"LOT: {selected_lot} {lot_count}")
        
        # Get other field values and counts in order
        field_names = ['PN', 'REF', 'MFG DATE', 'EXP DATE']
        for field_name in field_names:
            if hasattr(self, 'inspection_fields') and field_name in self.inspection_fields:
                field_value = self.inspection_fields[field_name].text().strip()
                if field_value and field_value != '-':
                    count_text = self.inspection_count_labels.get(field_name, QLabel()).text()
                    # Convert (x/y) format to [x/y] format and remove HTML tags
                    if '/' in count_text:
                        count_text = count_text.replace('<span style="color: #006400;">', '').replace('</span>', '').replace('<span style="color: black;">', '').replace('</span>', '')
                        count_text = f"[{count_text}]"
                    # Truncate long values
                    display_value = field_value[:20] + "..." if len(field_value) > 20 else field_value
                    lines.append(f"{field_name}: {display_value} {count_text}")
        
        # Draw text lines with appropriate spacing
        line_height = 28
        text_x = overlay_x + 15
        text_y = overlay_y + 30
        
        for i, line in enumerate(lines):
            painter.drawText(text_x, text_y + i * line_height, line)
    
    def reset_application(self):
        """Reset application to initial state"""
        # Clear current image and PDF
        self.current_image = None
        self.current_pdf_path = None
        
        # Clear OCR results
        self.ocr_results = []
        self.search_terms = {}
        
        # Reset image display
        self.image_label.clear()
        self.image_label.setText("이미지가 표시될 영역")
        self.image_label.setAlignment(Qt.AlignCenter)
        
        # Reset zoom
        self.zoom_slider.setValue(100)
        self.zoom_label.setText("100%")
        
        # Clear LOT dropdown and search
        self.lot_combo.clear()
        self.lot_combo.addItem("Select LOT...")
        self.lot_search_input.clear()
        
        # Reset inspection fields
        for field_name, field_widget in self.inspection_fields.items():
            field_widget.setText("-")
        
        # Reset inspection count labels
        for field_name, count_label in self.inspection_count_labels.items():
            count_label.setText("0")
            count_label.setStyleSheet("border: 1px solid gray; padding: 2px;")  # Remove green background
        
        # Reset inspection standard buttons
        for btn_name, btn in self.standard_buttons.items():
            btn.setChecked(False)
        self.current_standard = None
        
        # Clear Excel info
        self.excel_info_label.setText("")
        
        # Clear save status
        self.save_status_label.setText("")
        
        # Clear OCR text display in right panel
        if hasattr(self, 'ocr_text_display'):
            self.ocr_text_display.clear()
        
        print("Application reset to initial state")
    
    def on_lot_search_changed(self, text):
        """Handle LOT search input changes"""
        if not text.strip():
            return
        
        # Get all LOT items from dropdown (excluding "Select LOT...")
        lot_items = []
        for i in range(1, self.lot_combo.count()):  # Skip first item "Select LOT..."
            lot_items.append(self.lot_combo.itemText(i))
        
        # Search for matching LOT
        matching_lot = None
        
        # First try exact 8-digit match
        if len(text) == 8 and text.isdigit():
            for lot in lot_items:
                if lot.startswith(text):
                    matching_lot = lot
                    break
        
        # Then try last 4 digits match
        elif len(text) == 4 and text.isdigit():
            for lot in lot_items:
                if len(lot) >= 8 and lot[-4:] == text:
                    matching_lot = lot
                    break
        
        # Auto-select matching LOT
        if matching_lot:
            index = self.lot_combo.findText(matching_lot)
            if index >= 0:
                self.lot_combo.setCurrentIndex(index)
                
    @pyqtSlot(list)
    def on_ocr_completed(self, results):
        """Handle OCR completion"""
        self.ocr_results = results
        print(f"OCR completed with {len(results)} results")
        
        # Update Terminal display with OCR results
        self.update_ocr_text_display()
        
        # Auto-detect LOT number and populate dropdown
        self.auto_detect_lot_number(results)
        
        # Update GTIN field and highlighting
        self.update_gtin_field_and_highlighting(results)
        
        # Update highlighting and counts immediately
        self.update_inspection_highlighting()
        
        # Force another update after a short delay to ensure UI is refreshed
        QTimer.singleShot(200, self.force_highlighting_update)

    def auto_detect_lot_number(self, results):
        # Auto-detect LOT number from OCR results
        lot_number = self.extract_lot_from_ocr(results)
        if lot_number:
            self.auto_fill_lot_search(lot_number)

    def auto_fill_lot_search(self, lot_number):
        # Auto-fill LOT search input with detected LOT number
        self.lot_search_input.setText(lot_number)

    def extract_lot_from_ocr(self, results):
        """Extract LOT number from OCR results"""
        for result in results:
            text = result.get('text', '').strip()
            # Look for LOT patterns (8 digits typically)
            import re
            lot_pattern = r'\b\d{8}\b'
            match = re.search(lot_pattern, text)
            if match:
                return match.group()
        return None
    
    def update_gtin_field_and_highlighting(self, results):
        """Update GTIN field and add to search terms for highlighting"""
        print(f"DEBUG: Updating GTIN field with {len(results)} OCR results")
        
        # Extract GTIN directly from OCR results
        gtin_found = None
        gtin_count = 0
        
        import re
        for result in results:
            text = result.get('text', '').strip()
            # Look for (01) pattern followed by 14 digits
            match_01 = re.search(r'\(01\)(\d{14})', text)
            if match_01:
                extracted_gtin = match_01.group(1)
                if not gtin_found:
                    gtin_found = extracted_gtin
                    print(f"DEBUG: Found GTIN in OCR text '{text}' -> GTIN: {extracted_gtin}")
                gtin_count += 1
        
        # Update GTIN field only if not already set from Excel
        current_gtin = self.gtin_field.text().strip()
        if current_gtin == '-' or current_gtin == '':
            if gtin_found:
                self.gtin_field.setText(gtin_found)
                print(f"DEBUG: Updated GTIN field from OCR: {gtin_found}")
            else:
                self.gtin_field.setText("-")
                print("DEBUG: No GTIN found in OCR")
        else:
            print(f"DEBUG: GTIN field already set from Excel: {current_gtin}")
        
        # Set GTIN for highlighting (from field)
        display_gtin = self.gtin_field.text().strip()
        if display_gtin != '-':
            self.search_terms['GTIN'] = display_gtin
            
            # Count matching GTINs in OCR
            matching_count = 0
            for result in results:
                text = result.get('text', '').strip()
                match_01 = re.search(r'\(01\)(\d{14})', text)
                if match_01 and match_01.group(1) == display_gtin:
                    matching_count += 1
            
            # Update GTIN count label with reference comparison
            if self.current_standard and 'GTIN' in self.reference_counts.get(self.current_standard, {}):
                reference_count = self.reference_counts[self.current_standard]['GTIN']
                self.inspection_count_labels['GTIN'].setText(f'{matching_count}/{reference_count}')
                
                # Check if detected count matches reference count for highlighting
                if matching_count == reference_count:
                    self.inspection_count_labels['GTIN'].setStyleSheet("border: 1px solid gray; padding: 2px; background-color: #90EE90;")  # Light fluorescent green
                else:
                    self.inspection_count_labels['GTIN'].setStyleSheet("border: 1px solid gray; padding: 2px; background-color: #FFE4B5;")  # Light orange for mismatch
            else:
                self.inspection_count_labels['GTIN'].setText(str(matching_count))
                self.inspection_count_labels['GTIN'].setStyleSheet("border: 1px solid gray; padding: 2px;")
            print(f"DEBUG: GTIN highlighting set for: {display_gtin} (found {matching_count} matches)")
        else:
            self.inspection_count_labels['GTIN'].setText("0")
            if 'GTIN' in self.search_terms:
                del self.search_terms['GTIN']
    
    def extract_and_display_datamatrix(self, results):
        """Extract Data Matrix codes and display GTIN below image"""
        if hasattr(self.aws_textract_engine, 'extract_datamatrix_code'):
            datamatrix_codes = self.aws_textract_engine.extract_datamatrix_code(results)
            
            if datamatrix_codes:
                # Display first Data Matrix GTIN
                first_code = datamatrix_codes[0]
                gtin = first_code['gtin']
                
                # Update or create GTIN display label
                if not hasattr(self, 'gtin_label'):
                    self.gtin_label = QLabel()
                    self.gtin_label.setStyleSheet("""
                        QLabel {
                            background-color: #E3F2FD;
                            border: 2px solid #2196F3;
                            border-radius: 5px;
                            padding: 8px;
                            font-weight: bold;
                            font-size: 12px;
                            color: #1976D2;
                        }
                    """)
                    # Add to right panel layout
                    right_layout = self.right_panel.layout()
                    right_layout.addWidget(self.gtin_label)
                
                self.gtin_label.setText(f"Data Matrix GTIN: {gtin}")
                self.gtin_label.setVisible(True)
                print(f"Displayed Data Matrix GTIN: {gtin}")
            else:
                # Hide GTIN label if no Data Matrix found
                if hasattr(self, 'gtin_label'):
                    self.gtin_label.setVisible(False)
        
    def load_inspection_list(self):
        """Load Excel file with inspection data"""
        file_path, _ = QFileDialog.getOpenFileName(
            self, 
            "검사 목록 Excel 파일 선택", 
            "", 
            "Excel files (*.xlsx *.xls);;All files (*.*)"
        )
        
        if file_path:
            try:
                # Read Excel file
                df = pd.read_excel(file_path)
                
                # Validate columns
                expected_columns = ['LOT', 'PRODUCTS', 'PN', 'REF', 'MFG DATE', 'EXP DATE', 'GTIN']
                if not all(col in df.columns for col in expected_columns):
                    QMessageBox.warning(
                        self, 
                        "파일 형식 오류", 
                        f"Excel 파일에 필요한 컬럼이 없습니다.\n필요 컬럼: {', '.join(expected_columns)}"
                    )
                    return
                
                # Store data
                self.inspection_data = df.to_dict('records')
                
                
                # Populate LOT dropdown
                self.lot_combo.clear()
                self.lot_combo.addItem("Select LOT...")
                lot_values = df['LOT'].dropna().astype(str).tolist()
                self.lot_combo.addItems(lot_values)
                
                # Update status inline instead of showing dialog
                self.excel_info_label.setText(f"{os.path.basename(file_path)} - 총 {len(self.inspection_data)}개 항목 로딩 완료")
                self.excel_info_label.setStyleSheet("color: green; font-weight: bold; font-size: 10pt;")
                
            except Exception as e:
                QMessageBox.critical(
                    self, 
                    "파일 로딩 오류", 
                    f"Excel 파일을 읽는 중 오류가 발생했습니다:\n{str(e)}"
                )
    
    def on_lot_selected(self, selected_lot):
        """Handle LOT selection from dropdown"""
        if selected_lot == "Select LOT..." or not self.inspection_data:
            # Clear all fields
            for field in self.inspection_fields:
                self.inspection_fields[field].setText("-")
            return
        
        # Find matching record
        matching_record = None
        for record in self.inspection_data:
            if str(record.get('LOT', '')) == selected_lot:
                matching_record = record
                break
        
        if matching_record:
            # Update display fields
            self.inspection_fields['PRODUCTS'].setText(str(matching_record.get('PRODUCTS', '-')))
            self.inspection_fields['PN'].setText(str(matching_record.get('PN', '-')))
            self.inspection_fields['REF'].setText(str(matching_record.get('REF', '-')))
            
            # Update CHINA field if 중국 standard is selected
            if self.current_standard == '중국':
                self.update_china_field_value()
            
            # Format dates based on current inspection standard
            mfg_date = matching_record.get('MFG DATE', '-')
            exp_date = matching_record.get('EXP DATE', '-')
            
            # Determine date format based on inspection standard
            if self.current_standard == 'BSC':
                date_format = '%Y.%m.%d'  # YYYY.MM.DD for BSC
            else:
                date_format = '%Y-%m-%d'  # YYYY-MM-DD for MDR, MDD, 국내, 해외, 중국
            
            if pd.notna(mfg_date) and mfg_date != '-':
                try:
                    if isinstance(mfg_date, str):
                        mfg_date_formatted = mfg_date
                    else:
                        mfg_date_formatted = pd.to_datetime(mfg_date).strftime(date_format)
                except:
                    mfg_date_formatted = str(mfg_date)
            else:
                mfg_date_formatted = '-'
                
            if pd.notna(exp_date) and exp_date != '-':
                try:
                    if isinstance(exp_date, str):
                        exp_date_formatted = exp_date
                    else:
                        exp_date_formatted = pd.to_datetime(exp_date).strftime(date_format)
                except:
                    exp_date_formatted = str(exp_date)
            else:
                exp_date_formatted = '-'
            
            self.inspection_fields['MFG DATE'].setText(mfg_date_formatted)
            self.inspection_fields['EXP DATE'].setText(exp_date_formatted)
            
            # Update GTIN field from Excel data
            gtin_value = str(matching_record.get('GTIN', '-'))
            if gtin_value and gtin_value != 'nan':
                # Ensure GTIN is displayed with leading zeros (14 digits)
                try:
                    # Convert to int to remove any decimal points, then format with leading zeros
                    gtin_numeric = int(float(gtin_value))
                    gtin_formatted = f"{gtin_numeric:014d}"
                    self.gtin_field.setText(gtin_formatted)
                except (ValueError, TypeError):
                    # If conversion fails, use original value
                    self.gtin_field.setText(gtin_value)
            else:
                self.gtin_field.setText('-')
            
            # Auto-populate search fields
            self.populate_search_fields(matching_record, mfg_date_formatted, exp_date_formatted)
    
    def populate_search_fields(self, record, mfg_date_formatted, exp_date_formatted):
        """Populate search input fields with selected inspection data"""
        # Update highlighting for inspection fields
        self.update_inspection_highlighting()
    
    def set_inspection_standard(self, standard):
        """Set the current inspection standard and update reference counts"""
        # Uncheck all other buttons
        for name, btn in self.standard_buttons.items():
            btn.setChecked(name == standard)
        
        self.current_standard = standard
        print(f"Inspection standard set to: {standard}")
        
        # Update CHINA field visibility based on selected standard
        self.update_china_field_visibility()
        
        # Update reference counts display
        self.update_count_displays()
        
        # Update CHINA field value if 중국 standard is selected
        if standard == '중국':
            self.update_china_field_value()
        else:
            # Clear CHINA field for other standards
            if hasattr(self, 'inspection_fields') and 'CHINA' in self.inspection_fields:
                self.inspection_fields['CHINA'].setText('-')
            if 'CHINA' in self.search_terms:
                del self.search_terms['CHINA']
        
        # Update date formats for currently selected LOT
        selected_lot = self.lot_combo.currentText()
        if selected_lot and selected_lot != "Select LOT...":
            self.on_lot_selected(selected_lot)  # Refresh with new date format
        
        # Update count displays immediately
        self.update_count_displays()
    
    def update_count_displays(self):
        """Update count display format to show detected/reference counts"""
        if not self.current_standard:
            # No standard selected, show only detected counts
            for field, label in self.inspection_count_labels.items():
                if field in ['LOT', 'PRODUCTS', 'PN', 'REF', 'MFG DATE', 'EXP DATE', 'GTIN', 'CHINA']:
                    current_text = label.text()
                    if '/' not in current_text:
                        detected_count = current_text
                    else:
                        detected_count = current_text.split('/')[0]
                    label.setText(detected_count)
            return
        
        # Standard selected, show detected/reference format
        reference_counts = self.reference_counts[self.current_standard]
        
        for field, label in self.inspection_count_labels.items():
            if field in reference_counts:
                current_text = label.text()
                # Extract detected count, handling HTML formatted text for GTIN
                if field == 'GTIN' and '<span' in current_text:
                    # Extract number from HTML span for GTIN
                    import re
                    match = re.search(r'>(\d+)<', current_text)
                    detected_count = match.group(1) if match else current_text.split('/')[0] if '/' in current_text else current_text
                elif '/' not in current_text:
                    detected_count = current_text
                else:
                    detected_count = current_text.split('/')[0]
                
                reference_count = reference_counts[field]
                
                # Use plain text format for all fields
                label.setText(f"{detected_count}/{reference_count}")
                
                # Set green background if counts match
                try:
                    if int(detected_count) == reference_count:
                        label.setStyleSheet("border: 1px solid gray; padding: 2px; background-color: lightgreen;")
                    else:
                        label.setStyleSheet("border: 1px solid gray; padding: 2px;")
                except ValueError:
                    label.setStyleSheet("border: 1px solid gray; padding: 2px;")
            elif field == 'SEARCH':
                # SEARCH field doesn't have reference count
                current_text = label.text()
                if '/' in current_text:
                    detected_count = current_text.split('/')[0]
                else:
                    detected_count = current_text
                label.setText(detected_count)
                label.setStyleSheet("border: 1px solid gray; padding: 2px;")
    
    def update_inspection_counts(self):
        """Update count labels for inspection fields based on OCR results"""
        if not self.ocr_results:
            return
        
        # Count LOT dropdown value (실제 LOT 번호만 카운팅, 'LOT' 텍스트 제외)
        selected_lot = self.lot_combo.currentText()
        if selected_lot and selected_lot != "Select LOT...":
            count = 0
            excluded_words = ['Lasso', 'Stent', 'Delivery', 'Device', 'Use']
            for result in self.ocr_results:
                ocr_text = result.get('text', '').strip()
                # 'LOT' 필드명 자체는 제외하고 실제 LOT 번호만 카운팅
                # GTIN 바코드 텍스트는 제외 - GTIN 필드에서만 처리
                if ('(01)' not in ocr_text and  # GTIN 바코드 텍스트 제외
                    selected_lot.lower() in ocr_text.lower() and 
                    ocr_text.upper() not in ['LOT', 'PN', 'REF', 'MFG DATE', 'EXP DATE', 'PRODUCTS'] and
                    not ocr_text.upper().endswith(':') and  # 'LOT:' 같은 라벨 제외
                    len(ocr_text.strip()) > 2 and  # 너무 짧은 텍스트 제외
                    not any(excluded.lower() in ocr_text.lower() for excluded in excluded_words)):  # 제외 단어 필터링
                    count += 1
                    print(f"DEBUG: LOT 카운팅된 텍스트: '{ocr_text}' (검색값: '{selected_lot}')")
            
            if 'LOT' in self.inspection_count_labels:
                if self.current_standard and 'LOT' in self.reference_counts.get(self.current_standard, {}):
                    reference_count = self.reference_counts[self.current_standard]['LOT']
                    self.inspection_count_labels['LOT'].setText(f'{count}/{reference_count}')
                    
                    # Check if detected count matches reference count for highlighting
                    if count == reference_count:
                        self.inspection_count_labels['LOT'].setStyleSheet("border: 1px solid gray; padding: 2px; background-color: #90EE90;")  # Light fluorescent green
                    else:
                        self.inspection_count_labels['LOT'].setStyleSheet("border: 1px solid gray; padding: 2px; background-color: #FFE4B5;")  # Light orange for mismatch
                else:
                    self.inspection_count_labels['LOT'].setText(str(count))
                    self.inspection_count_labels['LOT'].setStyleSheet("border: 1px solid gray; padding: 2px;")
                print(f"DEBUG: LOT 카운트 업데이트: {count} (검색값: '{selected_lot}')")
        else:
            # Clear LOT count if no selection
            if 'LOT' in self.inspection_count_labels:
                self.inspection_count_labels['LOT'].setText("0")

        # Count SEARCH field value (검색어와 정확히 일치하는 값만 카운팅)
        search_text = self.search_input.text().strip() if hasattr(self, 'search_input') else ""
        if search_text:
            count = 0
            excluded_words = ['Lasso', 'Stent', 'Delivery', 'Device', 'Use']
            for result in self.ocr_results:
                ocr_text = result.get('text', '').strip()
                # 검색어가 포함된 텍스트만 카운팅 (필드명 제외)
                # GTIN 바코드 텍스트는 제외 - GTIN 필드에서만 처리
                if ('(01)' not in ocr_text and  # GTIN 바코드 텍스트 제외
                    search_text.lower() in ocr_text.lower() and 
                    ocr_text.upper() not in ['LOT', 'PN', 'REF', 'MFG DATE', 'EXP DATE', 'PRODUCTS'] and
                    not ocr_text.upper().endswith(':') and  # 라벨 형식 제외
                    len(ocr_text.strip()) > 2 and  # 너무 짧은 텍스트 제외
                    not any(excluded.lower() in ocr_text.lower() for excluded in excluded_words)):  # 제외 단어 필터링
                    count += 1
                    print(f"DEBUG: SEARCH 카운팅된 텍스트: '{ocr_text}' (검색어: '{search_text}')")
            # Update SEARCH count label
            if 'SEARCH' in self.inspection_count_labels:
                self.inspection_count_labels['SEARCH'].setText(str(count))
                print(f"DEBUG: SEARCH 카운트 업데이트: {count} (검색어: '{search_text}')")
        else:
            # Clear SEARCH count if no text
            if 'SEARCH' in self.inspection_count_labels:
                self.inspection_count_labels['SEARCH'].setText("0")

        # Count matches for each inspection field (실제 값만 카운팅, 필드명 제외)
        if hasattr(self, 'inspection_fields'):
            for field_name in self.inspection_fields:
                field_value = self.inspection_fields[field_name].text().strip()
                if field_value and field_value != '-':
                    count = 0
                    for result in self.ocr_results:
                        ocr_text = result.get('text', '').strip()

                        # 필드명 자체와 라벨 형식은 제외하고 실제 값만 수집
                        # GTIN 바코드 텍스트는 제외 - GTIN 필드에서만 처리
                        if ('(01)' not in ocr_text and  # GTIN 바코드 텍스트 제외
                            field_value.lower() in ocr_text.lower() and
                            ocr_text.upper() not in ['LOT', 'PN', 'REF', 'MFG DATE', 'EXP DATE', 'PRODUCTS'] and
                            not ocr_text.upper().endswith(':') and  # 'LOT:', 'REF:' 같은 라벨 제외
                            len(ocr_text.strip()) > 2 and  # 너무 짧은 텍스트 제외
                            not any(excluded.lower() in ocr_text.lower() for excluded in excluded_words)):  # 제외 단어 필터링
                            count += 1
                            print(f"DEBUG: 카운팅된 텍스트 - 필드: {field_name}, 값: '{field_value}', OCR 텍스트: '{ocr_text}'")

                    # Update count label if it exists
                    if field_name in self.inspection_count_labels:
                        if self.current_standard and field_name in self.reference_counts.get(self.current_standard, {}):
                            reference_count = self.reference_counts[self.current_standard][field_name]
                            self.inspection_count_labels[field_name].setText(f'{count}/{reference_count}')

                            # Check if detected count matches reference count for highlighting
                            if count == reference_count:
                                self.inspection_count_labels[field_name].setStyleSheet("border: 1px solid gray; padding: 2px; background-color: #90EE90;")  # Light fluorescent green
                            else:
                                self.inspection_count_labels[field_name].setStyleSheet("border: 1px solid gray; padding: 2px; background-color: #FFE4B5;")  # Light orange for mismatch
                        else:
                            self.inspection_count_labels[field_name].setText(str(count))
                            self.inspection_count_labels[field_name].setStyleSheet("border: 1px solid gray; padding: 2px;")
                    print(f"DEBUG: {field_name} 필드 카운트 업데이트: {count} (값: '{field_value}')")
                else:
                    # Clear count if no value
                    if field_name in self.inspection_count_labels:
                        self.inspection_count_labels[field_name].setText("0")
    
    def update_inspection_highlighting(self):
        """Update highlighting based on inspection field values"""
        if not self.ocr_results:
            return
        
        # Auto-select LOT if not already selected
        self.auto_select_lot_from_ocr()
        
        # Clear existing search terms
        self.search_terms = {}
        
        # Add LOT dropdown value first
        selected_lot = self.lot_combo.currentText()
        if selected_lot and selected_lot != "Select LOT...":
            self.search_terms['LOT'] = selected_lot
            print(f"DEBUG: Added LOT to search_terms: '{selected_lot}'")
        
        # Add inspection field values to search terms for highlighting first
        field_colors = {
            'PRODUCTS': 'gray', 'PN': 'green', 'REF': 'blue', 
            'MFG DATE': 'yellow', 'EXP DATE': 'purple'
        }
        
        for field_name, color in field_colors.items():
            if field_name in self.inspection_fields:
                field_value = self.inspection_fields[field_name].text().strip()
                if field_value and field_value != '-':
                    self.search_terms[field_name] = field_value
                    print(f"DEBUG: Added {field_name} to search_terms: '{field_value}'")
        
        # Add GTIN field value if available for exact matching
        gtin_value = self.gtin_field.text().strip()
        if gtin_value and gtin_value != '-':
            self.search_terms['GTIN'] = gtin_value
            print(f"DEBUG: Added GTIN to search_terms: '{gtin_value}'")
        
        # Add CHINA field value if available and current standard is 중국
        if self.current_standard == '중국' and 'CHINA' in self.inspection_fields:
            china_value = self.inspection_fields['CHINA'].text().strip()
            if china_value and china_value != '-':
                self.search_terms['CHINA'] = china_value
                print(f"DEBUG: Added CHINA to search_terms: '{china_value}'")
        
        # Add search input text if available
        search_text = self.search_input.text().strip()
        if search_text:
            # Use a unique color key for search that won't conflict
            self.search_terms['SEARCH'] = search_text
            print(f"DEBUG: Added SEARCH text: '{search_text}'")
        
        print(f"DEBUG: Final search_terms: {self.search_terms}")
        
        # Update counts and refresh display only once
        self.update_inspection_counts()
        if self.current_image is not None:
            self.display_image_with_overlays(self.current_image)
            
        # Force immediate update of highlighting after OCR completion
        QTimer.singleShot(100, self.force_highlighting_update)
    
    def auto_select_lot_from_ocr(self):
        """Auto-select LOT from OCR results if exact match found"""
        # Check if we have inspection data
        if not hasattr(self, 'inspection_data') or not self.inspection_data:
            return
        
        current_selection = self.lot_combo.currentText()
        
        # Skip if already processing or no valid selection state
        if hasattr(self, '_processing_auto_select') and self._processing_auto_select:
            return
        
        self._processing_auto_select = True
        
        # Get available LOT values from dropdown
        available_lots = []
        for i in range(1, self.lot_combo.count()):  # Skip "Select LOT..." item
            available_lots.append(self.lot_combo.itemText(i))
        
        if not available_lots:
            self._processing_auto_select = False
            return
        
        try:
            # Extract potential LOT candidates from OCR results
            lot_candidates = self.extract_lot_candidates_from_ocr()
            
            if not lot_candidates:
                return
            
            # Find exact matches first, then best partial matches
            best_match = self.find_best_lot_match(lot_candidates, available_lots)
            
            if best_match:
                # Check if this is a better match than current selection
                should_update = False
                
                if current_selection == "Select LOT...":
                    # No current selection, always update
                    should_update = True
                elif best_match['match_type'] == 'exact' and current_selection != best_match['lot']:
                    # Found exact match, and it's different from current
                    should_update = True
                    print(f"AUTO-SELECT: Upgrading from '{current_selection}' to exact match '{best_match['lot']}'")
                
                if should_update:
                    # Auto-select the matched LOT
                    lot_index = self.lot_combo.findText(best_match['lot'])
                    if lot_index >= 0:
                        print(f"AUTO-SELECT: Selecting '{best_match['lot']}' (type: {best_match['match_type']}) for OCR candidate '{best_match['candidate']}'")
                        self.lot_combo.setCurrentIndex(lot_index)
                        # Trigger the selection event manually
                        self.on_lot_selected(best_match['lot'])
                else:
                    print(f"AUTO-SELECT: Current selection '{current_selection}' is adequate, no change needed")
        
        finally:
            self._processing_auto_select = False
    
    def extract_lot_candidates_from_ocr(self):
        """Extract potential LOT candidates from OCR results"""
        candidates = []
        
        for item in self.ocr_results:
            text = item['text'].strip()
            confidence = item.get('confidence', 0)
            
            # Basic filtering
            if len(text) < 4 or confidence < 30:
                continue
            
            # LOT pattern matching - common patterns for medical device LOTs
            import re
            lot_patterns = [
                r'^\d{8}$',                    # 8 digits (like 25090776)
                r'^\d{2}[A-Z]\d{3}$',          # 2 digits + letter + 3 digits (like 24A001)
                r'^[A-Z]{2}\d{4}$',            # 2 letters + 4 digits
                r'^[A-Z0-9]{5,10}$'            # Alphanumeric 5-10 characters
            ]
            
            for pattern in lot_patterns:
                if re.match(pattern, text):
                    candidates.append({
                        'text': text,
                        'confidence': confidence,
                        'bbox': item.get('bbox', None)
                    })
                    print(f"LOT CANDIDATE: '{text}' (confidence: {confidence}%)")
                    break
        
        return candidates
    
    def find_best_lot_match(self, candidates, available_lots):
        """Find the best LOT match from candidates"""
        # First pass: Look for exact matches
        for candidate in candidates:
            candidate_text = candidate['text']
            if candidate_text in available_lots:
                return {
                    'lot': candidate_text,
                    'candidate': candidate_text,
                    'match_type': 'exact',
                    'confidence': candidate['confidence']
                }
        
        # Second pass: Look for partial matches (last 4 digits)
        best_match = None
        best_score = 0
        
        for candidate in candidates:
            candidate_text = candidate['text']
            
            # For numeric LOTs, try matching last 4 digits
            if len(candidate_text) >= 4 and candidate_text.isdigit():
                candidate_suffix = candidate_text[-4:]
                
                # Find all lots with matching suffix
                suffix_matches = []
                for lot in available_lots:
                    if len(lot) >= 4 and lot[-4:] == candidate_suffix:
                        suffix_matches.append(lot)
                
                if len(suffix_matches) == 1:
                    # Unique suffix match
                    return {
                        'lot': suffix_matches[0],
                        'candidate': candidate_text,
                        'match_type': 'suffix_unique',
                        'confidence': candidate['confidence']
                    }
                elif len(suffix_matches) > 1:
                    # Multiple suffix matches - find best one
                    for lot in suffix_matches:
                        score = self.calculate_lot_similarity_score(candidate_text, lot, candidate['confidence'])
                        if score > best_score:
                            best_score = score
                            best_match = {
                                'lot': lot,
                                'candidate': candidate_text,
                                'match_type': 'suffix_best',
                                'confidence': candidate['confidence'],
                                'score': score
                            }
        
        return best_match
    
    def calculate_lot_similarity_score(self, candidate, target, ocr_confidence):
        """Calculate similarity score between candidate and target LOT"""
        # String similarity (40% weight)
        from difflib import SequenceMatcher
        string_similarity = SequenceMatcher(None, candidate, target).ratio()
        
        # Prefix matching score (30% weight)
        prefix_score = 0
        min_len = min(len(candidate), len(target))
        if min_len > 0:
            matching_prefix = 0
            for i in range(min_len):
                if candidate[i] == target[i]:
                    matching_prefix += 1
                else:
                    break
            prefix_score = matching_prefix / min_len
        
        # OCR confidence (20% weight)
        confidence_score = ocr_confidence / 100
        
        # Length similarity (10% weight)
        length_diff = abs(len(candidate) - len(target))
        max_len = max(len(candidate), len(target))
        length_score = 1 - (length_diff / max_len) if max_len > 0 else 0
        
        # Calculate weighted total score
        total_score = (string_similarity * 0.4 + 
                      prefix_score * 0.3 + 
                      confidence_score * 0.2 + 
                      length_score * 0.1) * 100
        
        print(f"SCORE: '{candidate}' vs '{target}' = {total_score:.1f} "
              f"(str:{string_similarity:.2f}, pre:{prefix_score:.2f}, "
              f"conf:{confidence_score:.2f}, len:{length_score:.2f})")
        
        return total_score
    
    def force_highlighting_update(self):
        """Force update highlighting and counts after OCR completion"""
        if hasattr(self, 'ocr_results') and self.ocr_results:
            self.update_inspection_counts()
            if self.current_image is not None:
                self.display_image_with_overlays(self.current_image)
        
    @pyqtSlot(int)
    def update_progress(self, value):
        if value > 0:
            self.progress_bar.setVisible(True)
            self.progress_bar.setValue(value)
        else:
            self.progress_bar.setVisible(False)
    
    def show_pdf_navigation(self):
        """Show PDF navigation controls"""
        self.first_page_btn.setVisible(True)
        self.prev_page_btn.setVisible(True)
        self.next_page_btn.setVisible(True)
        self.last_page_btn.setVisible(True)
        self.page_info_label.setVisible(True)
        self.update_page_info()
        self.update_navigation_buttons()
    
    def hide_pdf_navigation(self):
        """Hide PDF navigation controls"""
        self.first_page_btn.setVisible(False)
        self.prev_page_btn.setVisible(False)
        self.next_page_btn.setVisible(False)
        self.last_page_btn.setVisible(False)
        self.page_info_label.setVisible(False)
    
    def update_page_info(self):
        """Update page information display"""
        page_info = self.pdf_handler.get_page_info()
        if page_info:
            current = page_info['current_page']
            total = page_info['total_pages']
            self.page_info_label.setText(f"{current} Page / {total} Page")
    
    def update_navigation_buttons(self):
        """Update navigation button states"""
        page_info = self.pdf_handler.get_page_info()
        if page_info:
            self.first_page_btn.setEnabled(page_info['has_previous'])
            self.prev_page_btn.setEnabled(page_info['has_previous'])
            self.next_page_btn.setEnabled(page_info['has_next'])
            self.last_page_btn.setEnabled(page_info['has_next'])
    
    def go_to_first_page(self):
        """Navigate to first page"""
        if self.pdf_handler.go_to_page(0):
            self.display_pdf()
    
    def go_to_previous_page(self):
        """Navigate to previous page"""
        if self.pdf_handler.previous_page():
            self.display_pdf()
    
    def go_to_next_page(self):
        """Navigate to next page"""
        if self.pdf_handler.next_page():
            self.display_pdf()
    
    def go_to_last_page(self):
        """Navigate to last page"""
        page_info = self.pdf_handler.get_page_info()
        if page_info:
            last_page = page_info['total_pages'] - 1
            if self.pdf_handler.go_to_page(last_page):
                self.display_pdf()
    
    def on_pdf_page_changed(self, page_number):
        """Handle PDF page change event"""
        self.update_page_info()
        self.update_navigation_buttons()
        
        # Automatically run OCR on new page with skew correction
        image = self.pdf_handler.get_current_page_image()
        if image is not None:
            # Apply automatic skew correction before OCR
            corrected_image = self.auto_skew_correction(image)
            self.current_image = corrected_image
            self.display_image_with_overlays(corrected_image)
            self.run_current_ocr_engine(corrected_image)
    
    def extract_lot_from_ocr(self, ocr_results):
        """Extract first 8-digit number from OCR results as LOT"""
        import re
        
        if not ocr_results:
            return None
        
        # Sort OCR results by position (top-left to bottom-right)
        sorted_results = sorted(ocr_results, key=lambda item: (item['bbox'][1], item['bbox'][0]))
        
        for item in sorted_results:
            text = item['text'].strip()
            
            # Look for 8-digit numbers (exactly 8 consecutive digits)
            eight_digit_pattern = r'\b\d{8}\b'
            matches = re.findall(eight_digit_pattern, text)
            
            if matches:
                lot_number = matches[0]
                print(f"Auto-detected LOT: {lot_number} from text: '{text}'")
                return lot_number
        
        return None
    
    def auto_fill_lot_search(self, lot_number):
        """Automatically fill LOT search field with last 4 digits"""
        if lot_number and len(lot_number) == 8:
            # Extract last 4 digits
            last_4_digits = lot_number[-4:]
            self.lot_search_input.setText(last_4_digits)
            print(f"Auto-filled LOT search with last 4 digits: {last_4_digits} (from full number: {lot_number})")
            
            # Update search terms and highlighting with last 4 digits
            self.search_terms['LOT'] = last_4_digits
            self.update_inspection_highlighting()


class OCRSettingsDialog(QDialog):
    """Dialog for OCR engine settings"""
    
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("OCR Settings")
        self.setModal(True)
        self.resize(400, 300)
        
        # Initialize default settings
        self.settings = {
            'min_confidence': 30,
            'image_scale_factor': 2.0,
            'enable_preprocessing': True,
            'small_text_enhancement': False
        }
        
        self.setup_ui()
    
    def setup_ui(self):
        layout = QFormLayout()
        
        # AWS Textract OCR settings
        general_label = QLabel("General OCR Settings")
        general_label.setFont(QFont("Arial", 10, QFont.Bold))
        layout.addRow(general_label)
        
        self.confidence_spin = QSpinBox()
        self.confidence_spin.setRange(0, 100)
        self.confidence_spin.setValue(self.settings['min_confidence'])
        layout.addRow("Minimum Confidence (%):", self.confidence_spin)
        
        self.scale_spin = QDoubleSpinBox()
        self.scale_spin.setRange(1.0, 5.0)
        self.scale_spin.setSingleStep(0.1)
        self.scale_spin.setValue(self.settings['image_scale_factor'])
        layout.addRow("Image Scale Factor:", self.scale_spin)
        
        self.preprocessing_check = QCheckBox()
        self.preprocessing_check.setChecked(self.settings['enable_preprocessing'])
        layout.addRow("Enable Image Preprocessing:", self.preprocessing_check)
        
        self.enhancement_check = QCheckBox()
        self.enhancement_check.setChecked(self.settings['small_text_enhancement'])
        layout.addRow("Small Text Enhancement:", self.enhancement_check)
        
        # AWS TEXTRACT settings
        aws_label = QLabel("AWS TEXTRACT Settings")
        aws_label.setFont(QFont("Arial", 10, QFont.Bold))
        layout.addRow(aws_label)
        
        aws_info_label = QLabel("AWS TEXTRACT uses cloud-based OCR with automatic optimization.")
        aws_info_label.setWordWrap(True)
        aws_info_label.setStyleSheet("color: gray; font-style: italic;")
        layout.addRow(aws_info_label)
        
        # Buttons
        button_layout = QHBoxLayout()
        ok_btn = QPushButton("OK")
        cancel_btn = QPushButton("Cancel")
        reset_btn = QPushButton("Reset to Defaults")
        
        ok_btn.clicked.connect(self.accept)
        cancel_btn.clicked.connect(self.reject)
        reset_btn.clicked.connect(self.reset_defaults)
        
        button_layout.addWidget(reset_btn)
        button_layout.addWidget(cancel_btn)
        button_layout.addWidget(ok_btn)
        
        layout.addRow(button_layout)
        self.setLayout(layout)
    
    def keyPressEvent(self, event):
        """Handle keyboard shortcuts for zoom control"""
        if event.key() == Qt.Key_A:
            # Zoom out by 30%
            current_zoom = self.zoom_slider.value()
            new_zoom = max(current_zoom - 30, 10)  # Minimum 10%
            self.zoom_slider.setValue(new_zoom)
        elif event.key() == Qt.Key_S:
            # Zoom in by 30%
            current_zoom = self.zoom_slider.value()
            new_zoom = min(current_zoom + 30, 500)  # Maximum 500%
            self.zoom_slider.setValue(new_zoom)
        else:
            super().keyPressEvent(event)
    
    def reset_defaults(self):
        """Reset all settings to default values"""
        self.confidence_spin.setValue(30)
        self.scale_spin.setValue(2.0)
        self.preprocessing_check.setChecked(True)
        self.enhancement_check.setChecked(False)
    
    def get_settings(self):
        """Get current settings from dialog"""
        return {
            'min_confidence': self.confidence_spin.value(),
            'image_scale_factor': self.scale_spin.value(),
            'enable_preprocessing': self.preprocessing_check.isChecked(),
            'small_text_enhancement': self.enhancement_check.isChecked()
        }
