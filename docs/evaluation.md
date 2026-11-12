# Evaluation: measuring verdicts, categories and markers

Wrong assignments and rework are the real cost of this tool, so its judgements have to be
measured, not guessed. `sfo eval` compares what the pipeline decides with a known truth, names
every wrong result on its own line, keeps every correction of the human as a test case, and
stores each run so that two runs can be compared.

Issue: https://github.com/crack666/smart_file_organizer/issues/36. The large measurement series
and model comparison build on it: https://github.com/crack666/smart_file_organizer/issues/10.

## Run it

With the local model (needs Ollama and the GPU; use a scratch database, not your real one):

```bash
sfo eval generate ~/sfo-eval                          # once: corpus and truth.json
sfo eval ~/sfo-eval --db ~/sfo-eval.db                # scan, analyse with the configured model, score
sfo eval ~/sfo-eval --db ~/sfo-eval.db --model <other-model> && sfo eval --compare --db ~/sfo-eval.db
```

Without a model, with scripted answers (what CI does):

```bash
sfo eval ~/sfo-eval --db /tmp/eval.db --stub ~/sfo-eval/truth.json
```

Every run creates a new scan job; `--model` and `--parallel` apply to that run only. Command
reference and JSON shapes: [cli.md](cli.md#sfo-eval-corpus).

## The corpus

`sfo eval generate <dir> [--seed n]` writes `<dir>/tree/` (the folder that is scanned) and
`<dir>/truth.json`. The generator is `SmallCorpusGenerator` (Infrastructure/Evaluation); the same
seed gives the same bytes and timestamps. Version 1 has 41 folders and 97 files:

| Kind | Example | Truth |
|---|---|---|
| Mixed storage | `Downloads`, `Desktop`, `alt` | `mixed` |
| Photo folders per occasion, names as people write them | `2019 Skifahrt`, `Sommerfest 2022`, `hochzeit anna und ben`, `100MEDIA` | `uniform` |
| Documents by topic | `Dokumente/Rechnungen 2023`, `Steuer 2022`, `Gesundheit`, `Briefe` | `uniform` |
| Software projects | `package.json`, `.git` | `unit`, also everything inside |
| Program and game | exe next to dll, exe next to `_Data` | `unit` |
| Phone dump | `DCIM/Camera`, `Android/data/<package>`, empty `LOST.DIR` | `unit`, also everything inside |
| Media | `Musik`, `Videos`, `Screenshots` | `uniform` |
| Placeholder | `Neuer Ordner` (empty) | `empty` |
| Hidden tool folder | `.ssh` | `system` |
| Sensitive dummies | password list, recovery codes, `.kdbx`, `.pfx`, `.env`, private key, bank statement, tax papers, doctor letter, ID scan, private letter | markers |

The structure follows what a manual check of a real private archive found
(https://github.com/crack666/smart_file_organizer/issues/29). Everything is invented: names,
texts and pictures. Images are small painted PNGs (photos keep camera-style `.jpg` names, because
the scan and the profile work on names and image decoders read the content); PDFs are minimal
text pages or one scanned-looking image; media, programs and archives are a header plus filler.
Sensitive dummies carry a real header line and random filler, assembled at run time, so neither
the repository nor the corpus contains a valid key or secret.

Change the corpus or its truth only together with `SmallCorpusGenerator.GeneratorVersion`, so
runs of different corpora are never compared as equal.

## The truth file

```json
{
  "schema_version": 1,
  "corpus": { "name": "small", "generator_version": 1, "seed": 36, "description": "..." },
  "root": "tree",
  "directories": [
    { "path": ".", "verdict": "mixed", "note": "Drive root: folders with different purposes." },
    { "path": "Fotos/2019 Skifahrt", "verdict": "uniform", "note": "Photos of one trip." }
  ],
  "files": [
    { "path": "Downloads/Kontoauszug_2024_03.pdf", "category": null, "sensitive": ["finance"],
      "note": "Category is invoice or document by the prompt; only the finance marker is measured." },
    { "path": "Fotos/2019 Skifahrt/IMG_20190212_101530.jpg", "category": "photo", "sensitive": [] }
  ]
}
```

- Paths are relative to `root`, with forward slashes; the scan root is `.`. Every folder and
  file on disk is listed exactly once; the reader refuses duplicates, unknown codes, a missing
  `sensitive` list and entries whose folder is not listed. Paths the scan finds but the truth
  does not list are reported as `untracked`.
- `verdict` and `sensitive` use the English codes of
  https://github.com/crack666/smart_file_organizer/issues/35; `category` is the category name in
  `snake_case` as in all CLI JSON (`document_scan`). They are compared through the enums
  (`EvalCodes`), never as strings. Until issue 35 switches the stored codes, reports show the
  current central codes (`gemischt`) for expected and actual alike.
- The truth must be unambiguous. `category: null` means no single category is right (a bank
  statement is `invoice` or `document` by the prompt) and only the markers are measured; files
  inside a unit have no category, because units are not classified file by file. `sensitive: []`
  means the file must not be marked. `note` says why, for people.

The same file shape serves as an answer sheet for `--stub`: the truth itself answers perfectly,
a copy with changed entries answers wrong on purpose. Where the truth leaves a category open, the
stub answers `document`, because a model has to commit to one as well.

## What is measured

| Kind | Items | Result compared |
|---|---|---|
| `directory_verdict` | every truth folder with a verdict | stored verdict, whether a rule, the model or the sample check decided |
| `file_category` | every truth file with a category | the model's category, or the one inherited from a confirmed sample (a human override is ignored) |
| `sensitive` | every truth file | markers of the rules and of the model together; a hit needs exactly the expected set |

Each item is a `hit`, `wrong` (a different result) or `missing` (not scanned, not analysed, or a
model error). For `sensitive` the score also counts files the truth marks, how many of them carry
every expected marker, how many lack one, and false alarms on unmarked files.

A failure names the item, the expectation, the result, who decided and why:

```text
directory_verdict wrong   Handy-Backup 2021/LOST.DIR: expected einheit, got leer  [rule empty]  No files and no subdirectories.
directory_verdict wrong   Fotos/Sommerfest 2022: expected einheitlich, got gemischt  [sample]  raised from einheitlich (model gemma4:26b): 5 of 6 sample files are Photo, 1 deviate, ...
file_category     wrong   Downloads/IMG_20230812_143012.jpg: expected photo, got screenshot  [model gemma4:26b]
sensitive         wrong   Desktop/wiederherstellungscodes.txt: expected zugangsdaten, got none  missed zugangsdaten
```

`[sample]` means the staged analysis overruled the pre-assessment: the sample of a uniform
folder contradicted it (or the model found a marker the rules had missed), so the folder became
mixed. For a file it means the category was inherited from a confirmed sample.

The `key` of a failure (`file_category:Fotos/...jpg`) is stable across runs and is what
`--compare` matches on.

## Test cases from corrections

When the human sets a category in the review (`ReviewService.ApplyOverrideAsync`, today the only
correction the product has) and it differs from the model's answer, `EvalCaseService` stores a
case in `eval_cases`: kind `file_category`, origin `review_override`, path, the right category,
the model's answer and model, and the input the model saw (file metadata, extracted text, image)
as `evidence_json`. Accepting the model's answer is no case; correcting the same file again
updates the case. Recording never fails the correction.

Sensitive files (marked by a rule, by the model, or by the name rules at the time of the
correction) get no evidence: the case keeps only path and markers. When such a case runs, the file
is read again locally if it still exists; otherwise the case is `missing`.

`sfo eval --cases` runs every case against the current model and prompts. A hit means the change
fixed the old mistake; `--compare` between two case runs shows cases that broke again.

Cases can hold private content. They live only in the local database (the same SQLite file as
the jobs) and are never exported: no command prints the evidence, and the output of a case is its
path, the codes and the outcome. Further correction kinds (a verdict overridden, a finding
reported as wrong) are added as new `EvalCaseKind` codes with their own runner once the product
has those corrections.

## Runs and comparison

Every `sfo eval` run is stored in `eval_runs`: mode (`corpus` or `cases`), subject
(`small v1 seed 36` or `cases`), start and end, job, model or stub, prompt fingerprint, app
version (with the commit when the build knows it), the settings and thresholds of the run
(`ollama.max_parallel_requests`, `ollama.max_samples_per_directory`, upload limits, timeout,
summary language, scan rules with hash) and the full report.

The prompt fingerprint (`OllamaService.PromptFingerprint`) is a hash over the system prompt, the
summary language, the prompt templates built for a fixed sample (classification with and without
the keep hint, pre-assessment, summary) and their answer schemas. Two runs with the same fingerprint asked the model the same way.

`sfo eval --compare` without ids compares the newest run with the newest earlier run of the same
mode and subject; with one id, that run with the one before it; with two ids, those two. It lists
what changed (model, prompts, version, each setting), the score per kind before and after, the
items fixed and the new failures.

## Measured

_12.11.2026, scripted answers (`--stub truth.json`), corpus small v1 seed 36, on the branch of
issue 36 rebased on the staged analysis:_ `directory_verdict` 32/41, `file_category` 49/49,
`sensitive` 96/97. (Before the rebase on the staged analysis the same run gave 35/41 verdicts;
the three folders below under "sample check" were hits then.) With perfect model answers every
remaining failure comes from rules, the sample check or the scan, not from the model:

- Sample check (3 failures). `Dokumente/Briefe` becomes mixed because the model marks
  `Brief_an_Oma.txt` as a private message the rules missed. `Dokumente/Persoenliches` (two
  personal documents and an ID scan) and `Fotos/Sommerfest 2022` (five photos and one video)
  deviate by 33 % and 17 %, above the 10 % limit. Whether the truth or the limit is right is open.
- The phone dump is not recognised as a unit. It has no direct files, so the model is never
  asked; the container rule makes `Handy-Backup 2021`, `DCIM`, `Android`, `Android/data` and the
  package folder `uniform`, and the empty `LOST.DIR` is `empty` (6 failures).
- `.ssh/id_ed25519` is not marked: the scan skips dot-folders (`skip_hidden`), so the key file is
  never listed and the `/.ssh/` marker rule never runs.

A run with the real model has not been made yet.
