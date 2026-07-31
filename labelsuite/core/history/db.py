"""검사 이력 SQLite DB — 결과 파일 카운터의 영속 저장소이기도 하다.

레거시는 파일 카운터가 실행마다 1로 리셋돼 이전 결과를 덮어썼다.
"""

from __future__ import annotations

import sqlite3
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

from labelsuite import __version__
from labelsuite.core.inspection import InspectionOutcome

_SCHEMA = """
CREATE TABLE IF NOT EXISTS inspections (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  ts TEXT NOT NULL,
  lot TEXT, ref TEXT, pn TEXT, products TEXT, gtin TEXT,
  standard TEXT, source TEXT,
  pdf_path TEXT, page INTEGER,
  passed INTEGER NOT NULL,
  image_path TEXT,
  app_version TEXT
);
CREATE TABLE IF NOT EXISTS inspection_fields (
  inspection_id INTEGER NOT NULL REFERENCES inspections(id) ON DELETE CASCADE,
  field TEXT NOT NULL, expected INTEGER, found INTEGER, passed INTEGER
);
CREATE TABLE IF NOT EXISTS barcode_checks (
  inspection_id INTEGER NOT NULL REFERENCES inspections(id) ON DELETE CASCADE,
  source TEXT, field TEXT, barcode_value TEXT, expected_value TEXT, matched INTEGER
);
CREATE TABLE IF NOT EXISTS counters (name TEXT PRIMARY KEY, value INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS idx_inspections_lot ON inspections(lot);
CREATE INDEX IF NOT EXISTS idx_inspections_ts ON inspections(ts);
"""


@dataclass
class InspectionRow:
    id: int
    ts: str
    lot: str
    ref: str
    pn: str
    products: str
    standard: str
    source: str
    page: int | None
    passed: bool
    image_path: str


@dataclass
class FieldRow:
    field: str
    expected: int | None
    found: int
    passed: bool


class HistoryDb:
    def __init__(self, path: Path | str):
        self.path = str(path)
        Path(self.path).parent.mkdir(parents=True, exist_ok=True)
        self._conn = sqlite3.connect(self.path)
        self._conn.execute("PRAGMA journal_mode=WAL")
        self._conn.execute("PRAGMA foreign_keys=ON")
        self._conn.executescript(_SCHEMA)
        self._conn.commit()

    def close(self) -> None:
        self._conn.close()

    # ---------- 기록 ----------

    def record_inspection(self, outcome: InspectionOutcome, image_path: str,
                          source: str, pdf_path: str | None = None,
                          page: int | None = None,
                          ts: datetime | None = None) -> int:
        record = outcome.record
        ts = ts or datetime.now()
        cursor = self._conn.execute(
            "INSERT INTO inspections (ts, lot, ref, pn, products, gtin, standard,"
            " source, pdf_path, page, passed, image_path, app_version)"
            " VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)",
            (ts.isoformat(timespec="seconds"), record.lot, record.ref, record.pn,
             record.products, record.gtin, outcome.standard.name, source,
             pdf_path, page, int(outcome.passed), image_path, __version__))
        inspection_id = cursor.lastrowid
        self._conn.executemany(
            "INSERT INTO inspection_fields (inspection_id, field, expected, found,"
            " passed) VALUES (?,?,?,?,?)",
            [(inspection_id, f.field, f.expected, f.found, int(f.passed))
             for f in outcome.fields.values()])
        self._conn.executemany(
            "INSERT INTO barcode_checks (inspection_id, source, field, barcode_value,"
            " expected_value, matched) VALUES (?,?,?,?,?,?)",
            [(inspection_id, c.source, c.field, c.barcode_value, c.expected_value,
              int(c.matched)) for c in outcome.barcode_checks])
        self._conn.commit()
        return inspection_id

    # ---------- 카운터 ----------

    def next_file_counter(self) -> int:
        with self._conn:
            self._conn.execute(
                "INSERT INTO counters (name, value) VALUES ('file_counter', 0)"
                " ON CONFLICT(name) DO NOTHING")
            self._conn.execute(
                "UPDATE counters SET value = value + 1 WHERE name = 'file_counter'")
            row = self._conn.execute(
                "SELECT value FROM counters WHERE name = 'file_counter'").fetchone()
        return int(row[0])

    # ---------- 조회 ----------

    def query(self, lot: str | None = None, date_from: str | None = None,
              date_to: str | None = None, passed: bool | None = None,
              limit: int = 500) -> list[InspectionRow]:
        sql = ("SELECT id, ts, lot, ref, pn, products, standard, source, page,"
               " passed, image_path FROM inspections WHERE 1=1")
        params: list = []
        if lot:
            sql += " AND lot LIKE ?"
            params.append(f"%{lot}%")
        if date_from:
            sql += " AND ts >= ?"
            params.append(date_from)
        if date_to:
            sql += " AND ts <= ?"
            params.append(date_to + "T23:59:59" if len(date_to) == 10 else date_to)
        if passed is not None:
            sql += " AND passed = ?"
            params.append(int(passed))
        sql += " ORDER BY ts DESC, id DESC LIMIT ?"
        params.append(limit)
        rows = self._conn.execute(sql, params).fetchall()
        return [InspectionRow(id=r[0], ts=r[1], lot=r[2] or "", ref=r[3] or "",
                              pn=r[4] or "", products=r[5] or "", standard=r[6] or "",
                              source=r[7] or "", page=r[8], passed=bool(r[9]),
                              image_path=r[10] or "") for r in rows]

    def fields_for(self, inspection_id: int) -> list[FieldRow]:
        rows = self._conn.execute(
            "SELECT field, expected, found, passed FROM inspection_fields"
            " WHERE inspection_id = ?", (inspection_id,)).fetchall()
        return [FieldRow(field=r[0], expected=r[1], found=r[2], passed=bool(r[3]))
                for r in rows]

    def delete(self, inspection_id: int) -> None:
        with self._conn:
            self._conn.execute("DELETE FROM inspections WHERE id = ?", (inspection_id,))

    def lots(self) -> list[str]:
        rows = self._conn.execute(
            "SELECT DISTINCT lot FROM inspections WHERE lot != '' ORDER BY lot").fetchall()
        return [r[0] for r in rows]
