"""앱 설정 관리 — 번들 기본값을 사용자 설정 디렉터리로 복사하고 로드/저장/마이그레이션.

쓰기 가능한 상태(설정, 이력 DB, OCR 캐시, 로그)는 전부 사용자 데이터 디렉터리에 둔다.
Windows: %APPDATA%/LabelSuite, Linux: ~/.config|~/.local/share 하위. 설치 폴더에는 쓰지 않는다.
"""

from __future__ import annotations

import json
import shutil
import sys
from pathlib import Path

from platformdirs import user_config_dir, user_data_dir

APP_NAME = "LabelSuite"

SETTINGS_FILE = "settings.json"
STANDARDS_FILE = "standards.json"
COLUMN_MAPS_FILE = "column_maps.json"
_CONFIG_FILES = (SETTINGS_FILE, STANDARDS_FILE, COLUMN_MAPS_FILE)


def resources_dir() -> Path:
    """번들 기본 설정 위치. PyInstaller frozen 실행이면 _MEIPASS 하위."""
    if getattr(sys, "frozen", False):
        return Path(sys._MEIPASS) / "labelsuite" / "resources" / "default_config"
    return Path(__file__).resolve().parent.parent / "resources" / "default_config"


def config_dir() -> Path:
    return Path(user_config_dir(APP_NAME, appauthor=False))


def data_dir() -> Path:
    return Path(user_data_dir(APP_NAME, appauthor=False))


def ensure_defaults(target: Path | None = None) -> Path:
    """설정 디렉터리를 만들고 없는 설정 파일은 번들 기본값으로 채운다."""
    target = target or config_dir()
    target.mkdir(parents=True, exist_ok=True)
    src = resources_dir()
    for name in _CONFIG_FILES:
        dst = target / name
        if not dst.exists():
            shutil.copyfile(src / name, dst)
    return target


def _read_json(path: Path) -> dict:
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _write_json(path: Path, data: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


class AppConfig:
    """settings/standards/column_maps 3개 JSON에 대한 접근 계층."""

    def __init__(self, directory: Path | None = None):
        self.directory = ensure_defaults(directory)
        self.settings: dict = _read_json(self.directory / SETTINGS_FILE)
        self.standards_raw: dict = _read_json(self.directory / STANDARDS_FILE)
        self.column_maps_raw: dict = _read_json(self.directory / COLUMN_MAPS_FILE)
        self._migrate()

    def _migrate(self) -> None:
        """번들 기본값에 새 키가 추가됐을 때 사용자 설정 파일을 보충한다."""
        defaults = _read_json(resources_dir() / SETTINGS_FILE)
        changed = False
        for key, value in defaults.items():
            if key not in self.settings:
                self.settings[key] = value
                changed = True
        if changed:
            self.save_settings()

        # 기존 사용자 standards.json에 새 규격 속성(display_name 등) 보충
        standards_defaults = _read_json(resources_dir() / STANDARDS_FILE)
        standards_changed = False
        user_specs = self.standards_raw.get("standards", {})
        for name, default_spec in standards_defaults.get("standards", {}).items():
            user_spec = user_specs.get(name)
            if not isinstance(user_spec, dict):
                continue
            for prop, value in default_spec.items():
                if prop == "counts":   # 사용자 편집값은 유지
                    continue
                if prop not in user_spec:
                    user_spec[prop] = value
                    standards_changed = True
        if standards_changed:
            self.save_standards()

    def save_settings(self) -> None:
        _write_json(self.directory / SETTINGS_FILE, self.settings)

    def save_standards(self) -> None:
        _write_json(self.directory / STANDARDS_FILE, self.standards_raw)

    def save_column_maps(self) -> None:
        _write_json(self.directory / COLUMN_MAPS_FILE, self.column_maps_raw)

    def restore_defaults(self, name: str) -> None:
        """지정한 설정 파일 하나를 번들 기본값으로 되돌린다."""
        shutil.copyfile(resources_dir() / name, self.directory / name)
        if name == SETTINGS_FILE:
            self.settings = _read_json(self.directory / name)
        elif name == STANDARDS_FILE:
            self.standards_raw = _read_json(self.directory / name)
        elif name == COLUMN_MAPS_FILE:
            self.column_maps_raw = _read_json(self.directory / name)

    def migrate_legacy_ligen_config(self, legacy_path: Path) -> bool:
        """레거시 LiGen의 app_config.json(파일 경로 3개)을 last_files로 이관."""
        try:
            legacy = _read_json(legacy_path)
        except (OSError, json.JSONDecodeError):
            return False
        files = legacy.get("files", {})
        changed = False
        for key in ("schedule", "product", "bsc"):
            path = files.get(key, "")
            if path and not self.settings["last_files"].get(key):
                self.settings["last_files"][key] = path
                changed = True
        if changed:
            self.save_settings()
        return changed
