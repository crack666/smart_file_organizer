namespace SmartFileOrganizer.Infrastructure.Persistence;

/// <summary>
/// All DDL for the SQLite schema in one place.
/// </summary>
internal static class Schema
{
    public const string CreateAll = """
        PRAGMA journal_mode=WAL;
        PRAGMA foreign_keys=ON;

        CREATE TABLE IF NOT EXISTS db_version (
            id      INTEGER PRIMARY KEY,
            version INTEGER NOT NULL,
            applied_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS scan_jobs (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            root_path               TEXT NOT NULL,
            status                  INTEGER NOT NULL DEFAULT 0,
            created_at              TEXT NOT NULL,
            started_at              TEXT,
            completed_at            TEXT,
            total_files             INTEGER NOT NULL DEFAULT 0,
            processed_files         INTEGER NOT NULL DEFAULT 0,
            total_directories       INTEGER NOT NULL DEFAULT 0,
            processed_directories   INTEGER NOT NULL DEFAULT 0,
            error_count             INTEGER NOT NULL DEFAULT 0,
            last_error              TEXT
        );

        CREATE TABLE IF NOT EXISTS directory_nodes (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id                  INTEGER NOT NULL REFERENCES scan_jobs(id),
            full_path               TEXT NOT NULL,
            name                    TEXT NOT NULL,
            parent_path             TEXT NOT NULL,
            root_path               TEXT NOT NULL,
            relative_path           TEXT NOT NULL,
            depth                   INTEGER NOT NULL DEFAULT 0,
            dir_status              INTEGER NOT NULL DEFAULT 0,
            dir_reason              TEXT,
            total_size              INTEGER NOT NULL DEFAULT 0,
            direct_file_count       INTEGER NOT NULL DEFAULT 0,
            recursive_file_count    INTEGER NOT NULL DEFAULT 0,
            subdir_count            INTEGER NOT NULL DEFAULT 0,
            dominant_file_types     TEXT,
            dominant_categories     TEXT,
            suggested_area          TEXT,
            scanned_at              TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_dir_nodes_job_path
            ON directory_nodes(job_id, full_path);
        CREATE INDEX IF NOT EXISTS idx_dir_nodes_parent
            ON directory_nodes(job_id, parent_path);

        CREATE TABLE IF NOT EXISTS file_nodes (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id          INTEGER NOT NULL REFERENCES scan_jobs(id),
            full_path       TEXT NOT NULL,
            name            TEXT NOT NULL,
            parent_path     TEXT NOT NULL,
            root_path       TEXT NOT NULL,
            relative_path   TEXT NOT NULL,
            relative_dir    TEXT NOT NULL,
            depth           INTEGER NOT NULL DEFAULT 0,
            size            INTEGER NOT NULL DEFAULT 0,
            last_write_time TEXT NOT NULL,
            extension       TEXT NOT NULL DEFAULT '',
            file_type       INTEGER NOT NULL DEFAULT 0,
            status          INTEGER NOT NULL DEFAULT 0,
            scanned_at      TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_file_nodes_job_path
            ON file_nodes(job_id, full_path);
        CREATE INDEX IF NOT EXISTS idx_file_nodes_parent
            ON file_nodes(job_id, parent_path);
        CREATE INDEX IF NOT EXISTS idx_file_nodes_status
            ON file_nodes(job_id, status);

        CREATE TABLE IF NOT EXISTS ai_results (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            file_node_id    INTEGER NOT NULL UNIQUE REFERENCES file_nodes(id),
            category        INTEGER NOT NULL DEFAULT 0,
            importance      INTEGER NOT NULL DEFAULT 0,
            confidence      REAL NOT NULL DEFAULT 0,
            summary         TEXT,
            suggested_target TEXT,
            model_used      TEXT,
            analyzed_at     TEXT NOT NULL,
            raw_response    TEXT,
            error           TEXT,
            is_ai_result    INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS user_overrides (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            file_node_id        INTEGER NOT NULL UNIQUE REFERENCES file_nodes(id),
            overridden_category INTEGER NOT NULL DEFAULT 0,
            overridden_target   TEXT,
            note                TEXT,
            overridden_at       TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ai_directory_results (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            directory_node_id   INTEGER NOT NULL REFERENCES directory_nodes(id),
            phase               TEXT NOT NULL,
            summary             TEXT,
            theme               TEXT,
            homogeneity         TEXT,
            dominant_type       TEXT,
            anomalous_file_ids  TEXT,
            sampling_strategy   TEXT,
            sample_size         INTEGER NOT NULL DEFAULT 0,
            model_used          TEXT,
            analyzed_at         TEXT NOT NULL,
            error               TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS idx_dir_results_node_phase
            ON ai_directory_results(directory_node_id, phase);
        """;
}
