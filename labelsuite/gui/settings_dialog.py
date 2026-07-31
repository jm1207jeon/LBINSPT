"""환경설정 다이얼로그 — 레거시에 하드코딩돼 있던 비즈니스 규칙의 편집 UI.

탭: 일반(경로/유효기간/프리페치), AWS(리전/프로필/인증 확인),
검사 기준(규격별 카운트 + 중국 REF 매핑), 컬럼 매핑(입력 엑셀 열/시트).
각 탭에 '기본값 복원' 제공. OK 시 저장하고, MainWindow가 재로딩한다.
"""

from __future__ import annotations

from PySide6.QtWidgets import (
    QComboBox,
    QDialog,
    QDialogButtonBox,
    QDoubleSpinBox,
    QFileDialog,
    QFormLayout,
    QGroupBox,
    QHBoxLayout,
    QHeaderView,
    QLabel,
    QLineEdit,
    QMessageBox,
    QPushButton,
    QSpinBox,
    QTableWidget,
    QTableWidgetItem,
    QTabWidget,
    QVBoxLayout,
    QWidget,
)

from labelsuite.core.config import (
    COLUMN_MAPS_FILE,
    SETTINGS_FILE,
    STANDARDS_FILE,
    AppConfig,
)

_COUNT_FIELDS = ["LOT", "PN", "REF", "MFG DATE", "EXP DATE", "GTIN", "CHINA"]


def _col_letter(index: int) -> str:
    """0-기반 인덱스 → 엑셀 열 문자 (참고 표시용)."""
    letters = ""
    index += 1
    while index:
        index, remainder = divmod(index - 1, 26)
        letters = chr(65 + remainder) + letters
    return letters


class SettingsDialog(QDialog):
    def __init__(self, config: AppConfig, parent=None):
        super().__init__(parent)
        self.config = config
        self.setWindowTitle("환경설정")
        self.resize(760, 560)

        layout = QVBoxLayout(self)
        self.tabs = QTabWidget()
        self.tabs.addTab(self._build_general_tab(), "일반")
        self.tabs.addTab(self._build_aws_tab(), "AWS")
        self.tabs.addTab(self._build_standards_tab(), "검사 기준")
        self.tabs.addTab(self._build_columns_tab(), "컬럼 매핑")
        layout.addWidget(self.tabs)

        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Ok
                                   | QDialogButtonBox.StandardButton.Cancel)
        buttons.accepted.connect(self._save_and_accept)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    # ---------------- 일반 ----------------

    def _build_general_tab(self) -> QWidget:
        tab = QWidget()
        form = QFormLayout(tab)

        save_row = QHBoxLayout()
        self.save_dir_edit = QLineEdit(self.config.settings.get("save_directory", ""))
        browse = QPushButton("찾아보기…")
        browse.clicked.connect(self._browse_save_dir)
        save_row.addWidget(self.save_dir_edit)
        save_row.addWidget(browse)
        form.addRow("결과 저장 폴더:", save_row)

        self.shelf_life_spin = QSpinBox()
        self.shelf_life_spin.setRange(1, 240)
        self.shelf_life_spin.setValue(int(self.config.settings.get("shelf_life_months", 36)))
        self.shelf_life_spin.setSuffix(" 개월")
        form.addRow("유효기간 (EXP = MFG + 기간 - 1일):", self.shelf_life_spin)

        self.prefetch_combo = QComboBox()
        self.prefetch_combo.addItems(["전체 페이지", "1페이지 앞", "2페이지 앞",
                                      "5페이지 앞", "프리페치 없음"])
        policy = self.config.settings.get("prefetch_policy", "all")
        index = {"all": 0, 1: 1, 2: 2, 5: 3, 0: 4}.get(policy, 0)
        self.prefetch_combo.setCurrentIndex(index)
        form.addRow("OCR 프리페치 범위:", self.prefetch_combo)

        self.render_zoom_spin = QDoubleSpinBox()
        self.render_zoom_spin.setRange(1.0, 8.0)
        self.render_zoom_spin.setSingleStep(0.5)
        self.render_zoom_spin.setValue(float(self.config.settings.get("pdf_render_zoom", 4.0)))
        form.addRow("PDF 렌더 배율 (72dpi 기준):", self.render_zoom_spin)

        restore = QPushButton("일반 설정 기본값 복원")
        restore.clicked.connect(lambda: self._restore(SETTINGS_FILE))
        form.addRow(restore)
        return tab

    def _browse_save_dir(self) -> None:
        path = QFileDialog.getExistingDirectory(self, "결과 저장 폴더",
                                                self.save_dir_edit.text())
        if path:
            self.save_dir_edit.setText(path)

    # ---------------- AWS ----------------

    def _build_aws_tab(self) -> QWidget:
        tab = QWidget()
        form = QFormLayout(tab)
        aws = self.config.settings.get("aws", {})
        self.aws_region_edit = QLineEdit(aws.get("region", "ap-northeast-2"))
        form.addRow("리전:", self.aws_region_edit)
        self.aws_profile_edit = QLineEdit(aws.get("profile", ""))
        self.aws_profile_edit.setPlaceholderText("비우면 기본 자격증명 체인 사용")
        form.addRow("프로필:", self.aws_profile_edit)

        check_row = QHBoxLayout()
        check_button = QPushButton("인증 확인")
        check_button.clicked.connect(self._check_aws)
        self.aws_check_label = QLabel("")
        check_row.addWidget(check_button)
        check_row.addWidget(self.aws_check_label, stretch=1)
        form.addRow(check_row)

        form.addRow(QLabel(
            "자격증명은 코드에 저장되지 않습니다.\n"
            "aws configure 또는 환경변수(AWS_ACCESS_KEY_ID 등)로 설정하세요."))
        return tab

    def _check_aws(self) -> None:
        from labelsuite.core.ocr.textract_client import TextractClient

        client = TextractClient(region=self.aws_region_edit.text().strip(),
                                profile=self.aws_profile_edit.text().strip() or None)
        self.aws_check_label.setText("확인 중…")
        self.repaint()
        status = client.validate_credentials()
        if status.ok:
            self.aws_check_label.setText(f"확인됨: {status.identity_arn}")
            self.aws_check_label.setStyleSheet("color: #007700;")
        else:
            self.aws_check_label.setText(f"실패: {status.error}")
            self.aws_check_label.setStyleSheet("color: #cc0000;")

    # ---------------- 검사 기준 ----------------

    def _build_standards_tab(self) -> QWidget:
        tab = QWidget()
        layout = QVBoxLayout(tab)

        counts_group = QGroupBox("규격별 필드 기대 출현 횟수")
        counts_layout = QVBoxLayout(counts_group)
        standards = self.config.standards_raw.get("standards", {})
        self.counts_table = QTableWidget(len(standards), len(_COUNT_FIELDS))
        self.counts_table.setHorizontalHeaderLabels(_COUNT_FIELDS)
        self.counts_table.setVerticalHeaderLabels(list(standards))
        for i, (name, spec) in enumerate(standards.items()):
            for j, field_name in enumerate(_COUNT_FIELDS):
                value = spec.get("counts", {}).get(field_name, 0)
                self.counts_table.setItem(i, j, QTableWidgetItem(str(value)))
        self.counts_table.horizontalHeader().setSectionResizeMode(
            QHeaderView.ResizeMode.Stretch)
        counts_layout.addWidget(self.counts_table)
        layout.addWidget(counts_group, stretch=3)

        china_group = QGroupBox("중국 REF 접두 → 등록번호 매핑")
        china_layout = QVBoxLayout(china_group)
        mapping = self.config.standards_raw.get("china_ref_mapping", {})
        self.china_table = QTableWidget(len(mapping) + 3, 2)
        self.china_table.setHorizontalHeaderLabels(["REF 접두(3자)", "등록번호 코드"])
        for i, (prefix, code) in enumerate(mapping.items()):
            self.china_table.setItem(i, 0, QTableWidgetItem(prefix))
            self.china_table.setItem(i, 1, QTableWidgetItem(code))
        self.china_table.horizontalHeader().setSectionResizeMode(
            QHeaderView.ResizeMode.Stretch)
        china_layout.addWidget(self.china_table)
        layout.addWidget(china_group, stretch=2)

        restore = QPushButton("검사 기준 기본값 복원")
        restore.clicked.connect(lambda: self._restore(STANDARDS_FILE))
        layout.addWidget(restore)
        return tab

    # ---------------- 컬럼 매핑 ----------------

    def _build_columns_tab(self) -> QWidget:
        tab = QWidget()
        layout = QVBoxLayout(tab)
        layout.addWidget(QLabel(
            "입력 엑셀의 열 위치(0-기반)와 시트명입니다. 열 문자는 참고용입니다."))
        self.column_spins: dict[tuple[str, str], QSpinBox] = {}
        self.sheet_edits: dict[str, QLineEdit] = {}
        labels = {"schedule": "주문일정 체크리스트", "product": "제품 품목번호 리스트",
                  "bsc": "BSC FGD 리스트"}
        for key, title in labels.items():
            entry = self.config.column_maps_raw.get(key, {})
            group = QGroupBox(title)
            form = QFormLayout(group)
            sheet_edit = QLineEdit(entry.get("sheet", ""))
            self.sheet_edits[key] = sheet_edit
            form.addRow("시트명:", sheet_edit)
            for col_name, col_index in entry.get("columns", {}).items():
                spin = QSpinBox()
                spin.setRange(0, 200)
                spin.setValue(int(col_index))
                hint = QLabel()
                spin.valueChanged.connect(
                    lambda v, h=hint: h.setText(f"{_col_letter(v)}열"))
                hint.setText(f"{_col_letter(int(col_index))}열")
                row = QHBoxLayout()
                row.addWidget(spin)
                row.addWidget(hint)
                row.addStretch(1)
                self.column_spins[(key, col_name)] = spin
                form.addRow(f"{col_name}:", row)
            layout.addWidget(group)
        restore = QPushButton("컬럼 매핑 기본값 복원")
        restore.clicked.connect(lambda: self._restore(COLUMN_MAPS_FILE))
        layout.addWidget(restore)
        return tab

    # ---------------- 저장/복원 ----------------

    def _restore(self, name: str) -> None:
        answer = QMessageBox.question(self, "기본값 복원",
                                      f"{name}을(를) 기본값으로 되돌릴까요?")
        if answer == QMessageBox.StandardButton.Yes:
            self.config.restore_defaults(name)
            QMessageBox.information(self, "복원 완료",
                                    "다이얼로그를 다시 열면 복원된 값이 표시됩니다.")

    def _save_and_accept(self) -> None:
        settings = self.config.settings
        settings["save_directory"] = self.save_dir_edit.text().strip()
        settings["shelf_life_months"] = self.shelf_life_spin.value()
        settings["prefetch_policy"] = ["all", 1, 2, 5, 0][
            self.prefetch_combo.currentIndex()]
        settings["pdf_render_zoom"] = self.render_zoom_spin.value()
        settings["aws"] = {"region": self.aws_region_edit.text().strip()
                                     or "ap-northeast-2",
                           "profile": self.aws_profile_edit.text().strip()}
        self.config.save_settings()

        # 검사 기준 테이블 반영
        standards = self.config.standards_raw.get("standards", {})
        for i, name in enumerate(standards):
            counts = standards[name].setdefault("counts", {})
            for j, field_name in enumerate(_COUNT_FIELDS):
                item = self.counts_table.item(i, j)
                try:
                    counts[field_name] = int(item.text()) if item else 0
                except ValueError:
                    counts[field_name] = 0
        mapping = {}
        for i in range(self.china_table.rowCount()):
            prefix_item = self.china_table.item(i, 0)
            code_item = self.china_table.item(i, 1)
            prefix = (prefix_item.text().strip().upper() if prefix_item else "")
            code = (code_item.text().strip() if code_item else "")
            if prefix and code:
                mapping[prefix] = code
        self.config.standards_raw["china_ref_mapping"] = mapping
        self.config.save_standards()

        # 컬럼 매핑 반영
        for key, edit in self.sheet_edits.items():
            self.config.column_maps_raw[key]["sheet"] = edit.text().strip()
        for (key, col_name), spin in self.column_spins.items():
            self.config.column_maps_raw[key]["columns"][col_name] = spin.value()
        self.config.save_column_maps()

        self.accept()
