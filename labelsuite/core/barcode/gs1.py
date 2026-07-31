"""GS1 Application Identifier 파서 — 순차 상태머신.

레거시 _parse_gs1_data는 "다음 AI"를 문자열 아무 위치에서나 검색해
GTIN 페이로드 안의 숫자쌍(10, 11, 17, 30)을 AI로 오인했다.
여기서는 커서 위치에서 최장 AI 접두를 매칭하고, 고정길이 AI는 정확히
N자를 소비, 가변길이 AI는 GS(0x1D) 구분자 또는 문자열 끝까지 소비한다.
"""

from __future__ import annotations

import calendar
import re
from dataclasses import dataclass, field
from datetime import date

GS = "\x1d"  # FNC1 그룹 구분자


@dataclass(frozen=True)
class AiSpec:
    ai: str
    fixed_len: int | None   # None = 가변길이
    max_len: int
    name: str


# 라벨에서 실제 쓰이는 AI 집합 (필요 시 확장)
AI_TABLE: dict[str, AiSpec] = {
    "00": AiSpec("00", 18, 18, "SSCC"),
    "01": AiSpec("01", 14, 14, "GTIN"),
    "02": AiSpec("02", 14, 14, "CONTENT GTIN"),
    "10": AiSpec("10", None, 20, "LOT"),
    "11": AiSpec("11", 6, 6, "PROD DATE"),
    "15": AiSpec("15", 6, 6, "BEST BEFORE"),
    "17": AiSpec("17", 6, 6, "EXP DATE"),
    "21": AiSpec("21", None, 20, "SERIAL"),
    "240": AiSpec("240", None, 30, "ADDITIONAL ID"),
    "30": AiSpec("30", None, 8, "VAR COUNT"),
    "310": AiSpec("310", 7, 7, "NET WEIGHT KG"),  # 3100~3105: 4번째 자리는 소수점 지시
}


class Gs1ParseError(ValueError):
    def __init__(self, message: str, position: int):
        super().__init__(f"{message} (위치 {position})")
        self.position = position


@dataclass
class Gs1Element:
    ai: str
    name: str
    value: str


@dataclass
class Gs1Message:
    elements: list[Gs1Element] = field(default_factory=list)

    def get(self, ai: str) -> str | None:
        for element in self.elements:
            if element.ai == ai:
                return element.value
        return None


def _strip_symbology_prefix(payload: str) -> str:
    """스캐너/디코더가 붙이는 심볼로지 식별자(]d2, ]C1, ]Q3 등) 제거."""
    if payload.startswith("]") and len(payload) >= 3:
        return payload[3:]
    return payload


def _normalize_parenthesized(payload: str) -> str:
    """사람이 읽는 '(01)0880...(10)ABC' 표기를 FNC1 표기로 변환.

    괄호 AI 표기가 하나라도 있으면 괄호를 구분자로 신뢰한다:
    가변길이 필드는 다음 '(' 앞에서 끝난 것으로 보고 GS를 삽입한다.
    """
    if "(" not in payload:
        return payload
    parts = re.split(r"\(([0-9]{2,4})\)", payload)
    # parts: ['', ai, value, ai, value, ...] — 값에 GS를 덧붙여 재조립
    out: list[str] = []
    for i in range(1, len(parts), 2):
        ai = parts[i]
        value = parts[i + 1] if i + 1 < len(parts) else ""
        out.append(ai + value + GS)
    return "".join(out)


def _match_ai(payload: str, pos: int) -> AiSpec | None:
    for length in (4, 3, 2):
        candidate = payload[pos:pos + length]
        if candidate in AI_TABLE:
            return AI_TABLE[candidate]
        # 소수점 지시 자릿수를 가진 AI(310x 등)는 3자리 접두로 매칭
        if len(candidate) == 4 and candidate[:3] in AI_TABLE and AI_TABLE[candidate[:3]].fixed_len:
            spec = AI_TABLE[candidate[:3]]
            return AiSpec(candidate, spec.fixed_len, spec.max_len, spec.name)
    return None


def parse_gs1(payload: str) -> Gs1Message:
    """FNC1(GS) 또는 괄호 표기의 GS1 데이터 문자열을 파싱한다."""
    payload = _strip_symbology_prefix(payload.strip())
    payload = _normalize_parenthesized(payload)
    message = Gs1Message()
    pos = 0
    length = len(payload)
    while pos < length:
        if payload[pos] == GS:  # 연속/선행 구분자 허용
            pos += 1
            continue
        spec = _match_ai(payload, pos)
        if spec is None:
            raise Gs1ParseError(f"알 수 없는 AI: {payload[pos:pos + 4]!r}", pos)
        pos += len(spec.ai)
        if spec.fixed_len is not None:
            value = payload[pos:pos + spec.fixed_len]
            if len(value) < spec.fixed_len:
                raise Gs1ParseError(
                    f"AI({spec.ai}) 값이 {spec.fixed_len}자보다 짧습니다: {value!r}", pos)
            pos += spec.fixed_len
        else:
            end = payload.find(GS, pos)
            if end == -1:
                end = length
            value = payload[pos:end]
            if len(value) > spec.max_len:
                raise Gs1ParseError(
                    f"AI({spec.ai}) 값이 최대 {spec.max_len}자를 초과합니다", pos)
            pos = end
        message.elements.append(Gs1Element(spec.ai, spec.name, value))
    if not message.elements:
        raise Gs1ParseError("GS1 데이터가 비어 있습니다", 0)
    return message


def parse_gs1_date(yymmdd: str) -> date | None:
    """GS1 날짜(YYMMDD). DD=00은 규칙대로 해당 월의 말일. 파싱 불가 시 None."""
    if not re.fullmatch(r"\d{6}", yymmdd or ""):
        return None
    yy, mm, dd = int(yymmdd[:2]), int(yymmdd[2:4]), int(yymmdd[4:6])
    year = 2000 + yy if yy <= 50 else 1900 + yy  # GS1 세기 규칙(±50년) 근사
    if not 1 <= mm <= 12:
        return None
    if dd == 0:
        dd = calendar.monthrange(year, mm)[1]
    try:
        return date(year, mm, dd)
    except ValueError:
        return None
