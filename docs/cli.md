# Command line: `sfo`

`sfo` drives the same core as the desktop app: scan a folder into the database, run the
AI phases, and read where a job stands. It is meant for people and for a controlling
model alike; everything a machine needs is in [Machine contract](#machine-contract).

```bash
dotnet run --project src/SmartFileOrganizer.Cli -- <command> [options]
# or, after a build:
dotnet src/SmartFileOrganizer.Cli/bin/Debug/net10.0/sfo.dll <command> [options]
```

Issue: https://github.com/crack666/smart_file_organizer/issues/5

## Global options

Accepted by every command.

| Option | Meaning |
|---|---|
| `--json` | Write exactly one JSON document to stdout. |
| `--progress none\|text\|jsonl` | Progress on stderr. Default `text`, or `none` with `--json`. |
| `--db <path>` | SQLite database. Overrides `SFO_DB`. |
| `-v`, `--verbose` | Log at information level (default: warnings and errors). |
| `-h`, `--help` | Help for the command. |

Database and settings are found as described in
[architecture.md, File locations](architecture.md#file-locations): `--db`, then `SFO_DB`, then
the per-user default. With a redirected database, settings are read from `settings.json` next
to it (or from `SFO_SETTINGS`), so a scratch database never touches the user's settings.
The CLI never writes `settings.json`; `--model` and `--parallel` apply to one run only.

## Commands

### `sfo scan <path>` / `sfo scan --resume <job>`

Creates a job for `<path>` and scans it, or continues an interrupted job. Give exactly one of
the two. Ctrl+C stores the job as `paused`; `--resume` continues it. Resuming a completed job
returns it unchanged.

```bash
sfo scan /media/usb-stick --json
```

```json
{
  "schema_version": 1,
  "command": "scan",
  "ok": true,
  "exit_code": 0,
  "result": {
    "job": {
      "id": 7,
      "root_path": "/media/usb-stick",
      "status": "completed",
      "created_at": "2026-11-12T13:36:42.138Z",
      "started_at": "2026-11-12T13:36:42.176Z",
      "completed_at": "2026-11-12T13:36:44.210Z",
      "files_total": 1830,
      "files_processed": 1830,
      "directories_total": 112,
      "directories_processed": 112,
      "error_count": 0,
      "last_error": null
    },
    "resumed": false
  },
  "error": null
}
```

`status` is one of `created`, `running`, `paused`, `completed`, `cancelled`, `failed`.
Errors: `path_not_found` (1), `job_not_found` (1), `scan_failed` (1, with `result`).

### `sfo analyze <job>`

Runs the AI phases for a job whose scan is `completed` and waits until they are done. Each
phase skips work already stored, so running it again after Ctrl+C or an error continues.

| Option | Meaning |
|---|---|
| `--phase <name>` | `pre_assessment`, `classification`, `summary` or `all` (default). Repeatable; phases always run in this order. |
| `--parallel <n>` | Concurrent Ollama requests, 1 to 8. Default: `Ollama.MaxParallelRequests`. |
| `--model <name>` | Ollama model for this run. Default: `Ollama.Model`. |

```bash
sfo analyze 7 --phase classification --parallel 2 --json
```

```json
{
  "schema_version": 1,
  "command": "analyze",
  "ok": true,
  "exit_code": 0,
  "result": {
    "job_id": 7,
    "model": "gemma4:26b",
    "parallel": 2,
    "phases": [
      { "phase": "classification", "total": 940, "processed": 940, "errors": 3,
        "message": "AI classification finished with 3 error(s)." }
    ],
    "error_count": 3
  },
  "error": null
}
```

Errors of single files or directories do not fail the command; they are counted here and
listed by `sfo status`. Failures: `ollama_unavailable` (3), `scan_not_completed` (1),
`job_not_found` (1), `cancelled` (130).

### `sfo jobs`

Lists all jobs, newest first.

```bash
sfo jobs --json
```

```json
{
  "schema_version": 1,
  "command": "jobs",
  "ok": true,
  "exit_code": 0,
  "result": { "jobs": [ { "id": 7, "root_path": "/media/usb-stick", "status": "completed", "...": "same fields as scan" } ] },
  "error": null
}
```

### `sfo status <job>`

Counters per phase and the most recent errors. `--max-errors <n>` (default 20) limits the list.

```bash
sfo status 7 --json
```

```json
{
  "schema_version": 1,
  "command": "status",
  "ok": true,
  "exit_code": 0,
  "result": {
    "job": { "id": 7, "status": "completed", "...": "same fields as scan" },
    "phases": {
      "scan": { "status": "completed", "files_total": 1830, "files_processed": 1830,
                "directories_total": 112, "directories_processed": 112, "errors": 0 },
      "pre_assessment": { "total": 104, "done": 104, "errors": 0, "pending": 0, "skipped": 0 },
      "classification": { "total": 940, "done": 610, "errors": 3, "pending": 0, "skipped": 327 },
      "summary": { "total": 104, "done": 0, "errors": 0, "pending": 104, "skipped": 0 }
    },
    "errors": [
      { "phase": "classification", "path": "/media/usb-stick/scan_004.pdf",
        "message": "Ollama request timed out after 180s." }
    ]
  },
  "error": null
}
```

For each AI phase, `total = done + errors + pending + skipped`.

- `pre_assessment` and `summary` count directories the AI phases consider: not skipped by the
  scan rules and not hidden (name starting with a dot).
- `classification` counts files of the types it handles (image, video, document). `skipped`
  are files the pre-assessment chose not to sample.

### `sfo doctor`

Checks the database (creates and migrates it if missing), whether Ollama answers within
5 seconds, and whether the configured model is installed.

```bash
sfo doctor --json
```

```json
{
  "schema_version": 1,
  "command": "doctor",
  "ok": false,
  "exit_code": 3,
  "result": {
    "ok": false,
    "database": { "path": "/home/me/.config/SmartFileOrganizer/data.db", "existed": true,
                  "schema_version": 1, "ok": true, "error": null },
    "settings_path": "/home/me/.config/SmartFileOrganizer/settings.json",
    "ollama": { "base_url": "http://localhost:11434", "reachable": false },
    "model": { "name": "gemma4:26b", "available": null, "installed": [] }
  },
  "error": { "code": "ollama_unavailable", "message": "Ollama is not reachable at http://localhost:11434." }
}
```

`model.available` is `null` when Ollama could not be asked. Exit code: 1 for a database
error (`database_error`), 3 if Ollama is unreachable, 1 if the model is missing
(`model_missing`), else 0.

## Machine contract

Applies to every command, including the ones added later.

**stdout.** With `--json`, exactly one JSON document, also on failure and on a wrong
invocation. Without `--json`, human-readable text that may change at any time.

**Envelope.**

| Field | Meaning |
|---|---|
| `schema_version` | Version of the document shapes, currently `1`. Raised when a field is removed or changes meaning; new fields can appear without a raise. |
| `command` | The command path, for example `scan`; `sfo` when no command was recognised. |
| `ok` | `true` exactly when `exit_code` is 0. |
| `exit_code` | Same as the process exit code. |
| `result` | Command-specific payload; `null` on most failures, present where it helps (the paused job after Ctrl+C during a scan, the doctor report). |
| `error` | `null`, or `{ "code": "...", "message": "..." }`. `code` is stable; `message` is for people. |

Field names are `snake_case`, enum values are `snake_case` strings, timestamps are ISO 8601
UTC with a trailing `Z`.

**stderr.** Logs and progress, never results. With `--progress jsonl`, every stderr line is
one JSON object with `schema_version` and `type`:

```json
{"schema_version":1,"type":"progress","phase":"scan","processed":120,"total":480,"errors":0,"current_path":"/media/usb-stick/photos","message":null}
{"schema_version":1,"type":"log","level":"warning","category":"ClassificationService","message":"...","exception":null}
```

`phase` is `scan`, `pre_assessment`, `classification` or `summary`. Progress is throttled to
one line per phase every 500 ms, plus the final line of a phase.

**Exit codes.**

| Code | Meaning | `error.code` |
|---|---|---|
| 0 | Done | |
| 1 | The command ran but could not do its job | `job_not_found`, `path_not_found`, `scan_failed`, `scan_not_completed`, `database_error`, `model_missing`, `internal_error` |
| 2 | Wrong invocation: unknown command or option, missing or invalid value | `usage` |
| 3 | Ollama not reachable | `ollama_unavailable` |
| 130 | Interrupted with Ctrl+C; stored work stays and the command can be run again | `cancelled` |

A second Ctrl+C terminates the process immediately.

## Adding a command

The contract is enforced in one place, so a new command does not repeat it:

1. Add `src/SmartFileOrganizer.Cli/Commands/<Name>Command.cs` with a static
   `Create(CliHost host, GlobalOptions globals)` that builds a `System.CommandLine.Command`
   and binds the handler with `CommandRunner.SetHandler`.
2. In the handler, get services from `ctx.GetServicesAsync(ct)`, report progress through
   `ctx.Progress`, and return a `CommandOutcome`: `Success(result, text)` or
   `Fail(exitCode, ErrorCodes.X, message)`. Throwing `CliFailureException` does the same.
   Never write to stdout.
3. Define the result as records in the command class; `CliJson` turns them into `snake_case`.
   Reuse `JobView` for jobs.
4. Register the command in `SfoApp.BuildRoot`, describe it here, and add tests in
   `tests/SmartFileOrganizer.Cli.Tests`.

`CommandRunner` turns the outcome into the envelope or text, maps Ctrl+C to 130 and any
unexpected exception to 1 (`internal_error`). Parse errors never reach the handler; `SfoApp`
answers them with exit code 2.
