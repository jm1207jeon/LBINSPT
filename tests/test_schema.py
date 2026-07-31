import pytest

from labelsuite.core.schema import (
    CANONICAL_COLUMNS,
    LabelRecord,
    SchemaError,
    date_format_for,
    load_inspection_list,
    normalize_gtin14,
    save_inspection_list,
)


class TestNormalizeGtin14:
    def test_thirteen_digit_string_padded(self):
        assert normalize_gtin14("8806173612345") == "08806173612345"

    def test_fourteen_digit_kept(self):
        assert normalize_gtin14("18806173612345") == "18806173612345"

    def test_excel_float_artifact(self):
        assert normalize_gtin14("8806173612345.0") == "08806173612345"

    def test_int_input(self):
        assert normalize_gtin14(8806173612345) == "08806173612345"

    def test_scientific_notation(self):
        assert normalize_gtin14("8.806173612345e12") == "08806173612345"

    def test_gs1_prefixed_text(self):
        assert normalize_gtin14("(01)08806173612345") == "08806173612345"

    def test_empty_and_nan(self):
        assert normalize_gtin14("") == ""
        assert normalize_gtin14(None) == ""
        assert normalize_gtin14("nan") == ""

    def test_too_long_rejected(self):
        with pytest.raises(SchemaError):
            normalize_gtin14("123456789012345")


class TestDateFormat:
    def test_bsc_uses_dots(self):
        assert date_format_for("BSC") == "%Y.%m.%d"

    def test_default_uses_hyphen(self):
        assert date_format_for("MDR") == "%Y-%m-%d"
        assert date_format_for(None) == "%Y-%m-%d"


def _sample_records():
    return [
        LabelRecord("24A1234", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
                    "2024-05-10", "2027-05-09", "08806173612345", standard="MDR"),
        LabelRecord("24B0001", "STENT Y", "HANARO-02", "BPJ01-01",
                    "2024.05.11", "2027.05.10", "08806173699999", standard="BSC"),
    ]


class TestRoundTrip:
    def test_eight_column_round_trip(self, tmp_path):
        path = tmp_path / "list8.xlsx"
        save_inspection_list(_sample_records(), str(path), include_standard=True)
        records, warnings = load_inspection_list(str(path))
        assert [r.lot for r in records] == ["24A1234", "24B0001"]
        assert records[0].standard == "MDR"
        assert records[1].standard == "BSC"
        assert records[0].gtin == "08806173612345"
        assert warnings == []

    def test_seven_column_legacy_file(self, tmp_path):
        """레거시 LiGen 형식(7컬럼, 13자리 GTIN)도 읽혀야 한다."""
        from openpyxl import Workbook

        path = tmp_path / "legacy.xlsx"
        wb = Workbook()
        ws = wb.active
        ws.title = "Label Inspection List"
        ws.append(list(CANONICAL_COLUMNS))
        ws.append(["24A1234", "HANAROSTENT X", "HANARO-01", "NCN20-080-230",
                   "2024-05-10", "2027-05-09", "8806173612345"])
        wb.save(path)

        records, _ = load_inspection_list(str(path))
        assert len(records) == 1
        assert records[0].standard is None
        assert records[0].gtin == "08806173612345"  # 13 → 14자리 정규화

    def test_wrong_header_raises(self, tmp_path):
        from openpyxl import Workbook

        path = tmp_path / "bad.xlsx"
        wb = Workbook()
        ws = wb.active
        ws.append(["LOT", "WRONG"])
        wb.save(path)
        with pytest.raises(SchemaError):
            load_inspection_list(str(path))


class TestConfigAndStandards:
    def test_defaults_copied_and_loaded(self, tmp_path):
        from labelsuite.core.config import AppConfig
        from labelsuite.core.standards import load_standards

        config = AppConfig(tmp_path / "cfg")
        bundle = load_standards(config)
        assert set(bundle.standards) == {"MDR", "MDD", "BSC", "A00", "A02", "중국"}
        assert bundle.spec("MDR").counts["LOT"] == 11
        assert bundle.spec("BSC").date_format == "%Y.%m.%d"
        assert bundle.spec("중국").uses_china_field is True
        assert bundle.china_code_for_ref("BPJ01-01") == "LBDB-04"
        assert bundle.china_code_for_ref("XXX") is None
        assert bundle.field_colors["LOT"] == (255, 0, 0, 100)

    def test_settings_migration_adds_new_keys(self, tmp_path):
        import json

        from labelsuite.core.config import AppConfig

        cfg_dir = tmp_path / "cfg"
        cfg_dir.mkdir(parents=True)
        (cfg_dir / "settings.json").write_text(
            json.dumps({"schema_version": 1}), encoding="utf-8")
        config = AppConfig(cfg_dir)
        assert "country_standard_map" in config.settings
        assert config.settings["prefetch_policy"] == "all"

    def test_legacy_ligen_migration(self, tmp_path):
        import json

        from labelsuite.core.config import AppConfig

        legacy = tmp_path / "app_config.json"
        legacy.write_text(json.dumps(
            {"files": {"schedule": "C:/x/sched.xlsx", "product": "", "bsc": "C:/x/bsc.xlsx"}}
        ), encoding="utf-8")
        config = AppConfig(tmp_path / "cfg")
        assert config.migrate_legacy_ligen_config(legacy) is True
        assert config.settings["last_files"]["schedule"] == "C:/x/sched.xlsx"
        assert config.settings["last_files"]["bsc"] == "C:/x/bsc.xlsx"
