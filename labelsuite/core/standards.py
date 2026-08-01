"""검사 규격(StandardSpec) 로딩 — standards.json이 단일 원천."""

from __future__ import annotations

from dataclasses import dataclass, field

from labelsuite.core.config import AppConfig


@dataclass(frozen=True)
class StandardSpec:
    name: str
    counts: dict[str, int] = field(default_factory=dict)
    date_format: str = "%Y-%m-%d"
    uses_china_field: bool = False
    display_name: str = ""   # UI 표시명 — 내부 키(name)는 목록 파일 호환을 위해 불변

    @property
    def label(self) -> str:
        return self.display_name or self.name


@dataclass(frozen=True)
class StandardsBundle:
    standards: dict[str, StandardSpec]
    china_ref_mapping: dict[str, str]
    field_colors: dict[str, tuple[int, int, int, int]]

    def spec(self, name: str) -> StandardSpec:
        try:
            return self.standards[name]
        except KeyError:
            raise KeyError(f"정의되지 않은 검사 규격: {name!r}")

    def china_code_for_ref(self, ref: str) -> str | None:
        """REF 접두 3자로 중국 등록번호 코드를 찾는다 (레거시 china_ref_mapping)."""
        prefix = (ref or "")[:3].upper()
        return self.china_ref_mapping.get(prefix)


def load_standards(config: AppConfig) -> StandardsBundle:
    raw = config.standards_raw
    standards = {
        name: StandardSpec(
            name=name,
            counts=dict(spec.get("counts", {})),
            date_format=spec.get("date_format", "%Y-%m-%d"),
            uses_china_field=bool(spec.get("uses_china_field", False)),
            display_name=spec.get("display_name", ""),
        )
        for name, spec in raw.get("standards", {}).items()
    }
    colors = {
        fname: tuple(rgba)
        for fname, rgba in raw.get("field_colors", {}).items()
    }
    return StandardsBundle(
        standards=standards,
        china_ref_mapping=dict(raw.get("china_ref_mapping", {})),
        field_colors=colors,
    )
