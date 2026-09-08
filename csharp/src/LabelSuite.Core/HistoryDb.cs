// 검사 이력 SQLite DB + 결과 파일 카운터 (파이썬 core/history/db.py 포팅).
using Microsoft.Data.Sqlite;

namespace LabelSuite.Core;

public sealed record InspectionRow(
    long Id, string Ts, string Lot, string Ref, string Pn, string Products,
    string Standard, string Source, int? Page, bool Passed, string ImagePath);

public sealed record FieldRow(string Field, int? Expected, int Found, bool Passed);

public sealed class HistoryDb : IDisposable
{
    /// <summary>inspections.app_version에 기록할 빌드 식별자 — 앱 시작 시 CI 정보 버전으로 설정한다.</summary>
    public static string AppVersion { get; set; } = "1.0.0";

    private const string SchemaSql = """
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
        CREATE TABLE IF NOT EXISTS label_master (
          pn TEXT PRIMARY KEY,
          products TEXT, ref TEXT, gtin TEXT,
          expected_symbologies TEXT,   -- 쉼표 구분 (예: 'GS1 DataMatrix,GS1-128')
          note TEXT,
          updated_at TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_inspections_lot ON inspections(lot);
        CREATE INDEX IF NOT EXISTS idx_inspections_ts ON inspections(ts);
        """;

    private readonly SqliteConnection _connection;

    public HistoryDb(string path)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA foreign_keys=ON");
        Execute(SchemaSql);
    }

    private void Execute(string sql, params (string, object?)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public long RecordInspection(InspectionOutcome outcome, string imagePath,
                                 string source, string? pdfPath = null, int? page = null,
                                 DateTime? ts = null)
    {
        var record = outcome.Record;
        using var transaction = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO inspections (ts, lot, ref, pn, products, gtin, standard,
              source, pdf_path, page, passed, image_path, app_version)
            VALUES ($ts,$lot,$ref,$pn,$products,$gtin,$standard,$source,$pdf,$page,$passed,$img,$ver);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$ts", (ts ?? DateTime.Now).ToString("yyyy-MM-ddTHH:mm:ss"));
        insert.Parameters.AddWithValue("$lot", record.Lot);
        insert.Parameters.AddWithValue("$ref", record.Ref);
        insert.Parameters.AddWithValue("$pn", record.Pn);
        insert.Parameters.AddWithValue("$products", record.Products);
        insert.Parameters.AddWithValue("$gtin", record.Gtin);
        insert.Parameters.AddWithValue("$standard", outcome.Standard.Name);
        insert.Parameters.AddWithValue("$source", source);
        insert.Parameters.AddWithValue("$pdf", (object?)pdfPath ?? DBNull.Value);
        insert.Parameters.AddWithValue("$page", (object?)page ?? DBNull.Value);
        insert.Parameters.AddWithValue("$passed", outcome.Passed ? 1 : 0);
        insert.Parameters.AddWithValue("$img", imagePath);
        insert.Parameters.AddWithValue("$ver", AppVersion);
        var id = (long)insert.ExecuteScalar()!;

        foreach (var field in outcome.Fields.Values)
        {
            using var fieldInsert = _connection.CreateCommand();
            fieldInsert.Transaction = transaction;
            fieldInsert.CommandText = """
                INSERT INTO inspection_fields (inspection_id, field, expected, found, passed)
                VALUES ($id,$field,$expected,$found,$passed)
                """;
            fieldInsert.Parameters.AddWithValue("$id", id);
            fieldInsert.Parameters.AddWithValue("$field", field.Field);
            fieldInsert.Parameters.AddWithValue("$expected", (object?)field.Expected ?? DBNull.Value);
            fieldInsert.Parameters.AddWithValue("$found", field.Found);
            fieldInsert.Parameters.AddWithValue("$passed", field.Passed ? 1 : 0);
            fieldInsert.ExecuteNonQuery();
        }
        foreach (var check in outcome.BarcodeChecks)
        {
            using var checkInsert = _connection.CreateCommand();
            checkInsert.Transaction = transaction;
            checkInsert.CommandText = """
                INSERT INTO barcode_checks (inspection_id, source, field, barcode_value,
                  expected_value, matched) VALUES ($id,$src,$field,$val,$exp,$matched)
                """;
            checkInsert.Parameters.AddWithValue("$id", id);
            checkInsert.Parameters.AddWithValue("$src", check.Source);
            checkInsert.Parameters.AddWithValue("$field", check.Field);
            checkInsert.Parameters.AddWithValue("$val", check.BarcodeValue);
            checkInsert.Parameters.AddWithValue("$exp", check.ExpectedValue);
            checkInsert.Parameters.AddWithValue("$matched", check.Matched ? 1 : 0);
            checkInsert.ExecuteNonQuery();
        }
        transaction.Commit();
        return id;
    }

    /// <summary>결과 파일 일련번호 — 재실행에도 이어진다 (레거시 리셋 버그 해결).</summary>
    public int NextFileCounter()
    {
        using var transaction = _connection.BeginTransaction();
        Execute2(transaction,
            "INSERT INTO counters (name, value) VALUES ('file_counter', 0) " +
            "ON CONFLICT(name) DO NOTHING");
        Execute2(transaction,
            "UPDATE counters SET value = value + 1 WHERE name = 'file_counter'");
        using var select = _connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT value FROM counters WHERE name = 'file_counter'";
        var value = Convert.ToInt32(select.ExecuteScalar());
        transaction.Commit();
        return value;
    }

    private void Execute2(SqliteTransaction transaction, string sql)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public List<InspectionRow> Query(string? lot = null, bool? passed = null, int limit = 500)
    {
        using var command = _connection.CreateCommand();
        var sql = "SELECT id, ts, lot, ref, pn, products, standard, source, page, passed," +
                  " image_path FROM inspections WHERE 1=1";
        if (!string.IsNullOrEmpty(lot))
        {
            sql += " AND lot LIKE $lot";
            command.Parameters.AddWithValue("$lot", $"%{lot}%");
        }
        if (passed is not null)
        {
            sql += " AND passed = $passed";
            command.Parameters.AddWithValue("$passed", passed.Value ? 1 : 0);
        }
        sql += " ORDER BY ts DESC, id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<InspectionRow>();
        while (reader.Read())
            rows.Add(new InspectionRow(
                reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4),
                reader.IsDBNull(5) ? "" : reader.GetString(5),
                reader.IsDBNull(6) ? "" : reader.GetString(6),
                reader.IsDBNull(7) ? "" : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8),
                reader.GetInt32(9) != 0,
                reader.IsDBNull(10) ? "" : reader.GetString(10)));
        return rows;
    }

    public List<FieldRow> FieldsFor(long inspectionId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT field, expected, found, passed FROM inspection_fields" +
                              " WHERE inspection_id = $id";
        command.Parameters.AddWithValue("$id", inspectionId);
        using var reader = command.ExecuteReader();
        var rows = new List<FieldRow>();
        while (reader.Read())
            rows.Add(new FieldRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt32(1),
                reader.GetInt32(2), reader.GetInt32(3) != 0));
        return rows;
    }

    public void Delete(long inspectionId) =>
        Execute("DELETE FROM inspections WHERE id = $id", ("$id", inspectionId));

    public List<string> Lots()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT lot FROM inspections WHERE lot != '' ORDER BY lot";
        using var reader = command.ExecuteReader();
        var lots = new List<string>();
        while (reader.Read()) lots.Add(reader.GetString(0));
        return lots;
    }

    // ---------- 라벨 기준정보(마스터) DB ----------

    /// <summary>목록 로드 시 PN별 기준정보를 자동 축적한다.
    /// 이미 등록된 PN의 REF/GTIN은 덮어쓰지 않는다 (사전 등록값이 기준).</summary>
    public void UpsertMasterFromRecord(LabelRecord record)
    {
        if (record.Pn.Trim().Length == 0) return;
        Execute("""
            INSERT INTO label_master (pn, products, ref, gtin, updated_at)
            VALUES ($pn, $products, $ref, $gtin, $ts)
            ON CONFLICT(pn) DO UPDATE SET
              products = CASE WHEN label_master.products IS NULL OR label_master.products = ''
                              THEN excluded.products ELSE label_master.products END,
              ref = CASE WHEN label_master.ref IS NULL OR label_master.ref = ''
                         THEN excluded.ref ELSE label_master.ref END,
              gtin = CASE WHEN label_master.gtin IS NULL OR label_master.gtin = ''
                          THEN excluded.gtin ELSE label_master.gtin END
            """,
            ("$pn", record.Pn.Trim()), ("$products", record.Products),
            ("$ref", record.Ref), ("$gtin", record.Gtin),
            ("$ts", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")));
    }

    public void SaveMasterRow(MasterRow row)
    {
        if (row.Pn.Trim().Length == 0) return;
        Execute("""
            INSERT INTO label_master (pn, products, ref, gtin, expected_symbologies,
                                      note, updated_at)
            VALUES ($pn, $products, $ref, $gtin, $syms, $note, $ts)
            ON CONFLICT(pn) DO UPDATE SET
              products = excluded.products, ref = excluded.ref, gtin = excluded.gtin,
              expected_symbologies = excluded.expected_symbologies,
              note = excluded.note, updated_at = excluded.updated_at
            """,
            ("$pn", row.Pn.Trim()), ("$products", row.Products), ("$ref", row.Ref),
            ("$gtin", row.Gtin), ("$syms", row.ExpectedSymbologies), ("$note", row.Note),
            ("$ts", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")));
    }

    public void DeleteMaster(string pn) =>
        Execute("DELETE FROM label_master WHERE pn = $pn", ("$pn", pn));

    public MasterRow? GetMaster(string pn)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT pn, products, ref, gtin, expected_symbologies," +
                              " note FROM label_master WHERE pn = $pn";
        command.Parameters.AddWithValue("$pn", pn.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadMasterRow(reader) : null;
    }

    public List<MasterRow> AllMaster()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT pn, products, ref, gtin, expected_symbologies," +
                              " note FROM label_master ORDER BY pn";
        using var reader = command.ExecuteReader();
        var rows = new List<MasterRow>();
        while (reader.Read()) rows.Add(ReadMasterRow(reader));
        return rows;
    }

    private static MasterRow ReadMasterRow(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? "" : reader.GetString(1),
        reader.IsDBNull(2) ? "" : reader.GetString(2),
        reader.IsDBNull(3) ? "" : reader.GetString(3),
        reader.IsDBNull(4) ? "" : reader.GetString(4),
        reader.IsDBNull(5) ? "" : reader.GetString(5));

    public void Dispose() => _connection.Dispose();
}

public sealed record MasterRow(string Pn, string Products, string Ref, string Gtin,
                               string ExpectedSymbologies, string Note);

/// <summary>사전 등록된 마스터 기준정보와 검사 대상/검출 결과를 대조한다.</summary>
public static class MasterCheck
{
    public const string Source = "기준DB";

    /// <summary>목록 레코드가 마스터와 일치하는지 + 기대 심볼로지가 검출됐는지.</summary>
    public static List<CrossCheckResult> Check(MasterRow master, LabelRecord record,
                                               IReadOnlyList<BarcodeHit> detected)
    {
        var checks = new List<CrossCheckResult>();
        if (master.Ref.Trim().Length > 0)
            checks.Add(new CrossCheckResult(Source, "REF", record.Ref, master.Ref,
                record.Ref.Trim() == master.Ref.Trim()));
        if (master.Gtin.Trim().Length > 0)
        {
            var expected = Schema.NormalizeGtin14(master.Gtin);
            var actual = record.Gtin.Length > 0 ? Schema.NormalizeGtin14(record.Gtin) : "";
            checks.Add(new CrossCheckResult(Source, "GTIN", actual, expected,
                                            actual == expected));
        }
        foreach (var symbology in master.ExpectedSymbologies.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var found = detected.Any(h => string.Equals(
                h.Symbology, symbology, StringComparison.OrdinalIgnoreCase));
            checks.Add(new CrossCheckResult(Source, "심볼로지",
                found ? symbology : "미검출", symbology, found));
        }
        return checks;
    }
}
