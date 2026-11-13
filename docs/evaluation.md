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
seed gives the same bytes and timestamps. Version 4 has 73 folders and 174 files (version 3: 62 and
160, version 2: 42 and 100, version 1: 41 and 97; the changes are listed under [Truth changes](#truth-changes)):

| Kind | Example | Truth |
|---|---|---|
| Mixed storage | `Downloads`, `Desktop`, `alt` | `mixed` |
| Photo folders per occasion, names as people write them | `2019 Skifahrt`, `Sommerfest 2022`, `hochzeit anna und ben`, `100MEDIA` | `uniform` |
| Documents by topic | `Dokumente/Rechnungen 2023`, `Steuer 2022`, `Bank`, `Gesundheit`, `Briefe` | `uniform` |
| Software projects | `package.json`, `.git` | `unit`, also everything inside |
| Program and game | exe next to dll, exe next to `_Data` | `unit` |
| Phone dump | `DCIM/Camera`, `Android/data/<package>`, empty `LOST.DIR` | `dump`; camera `uniform`, app data `system`, `LOST.DIR` `empty` |
| User profiles of an old computer (version 3) | `Profile alter PC/max` (`AppData`, `Documents`, `Desktop`, `Downloads`, `Pictures`, `NTUSER.DAT`), `Profile alter PC/lisa` (old style: `Anwendungsdaten`, `Lokale Einstellungen/Temp`, `Cookies`, `Eigene Dateien`) | each `dump`; documents, desktop, pictures judged like any folder, app data, caches and cookies `system`; the folder of both `uniform` |
| Places of programs and Windows (version 4) | `Profile alter PC/Public` with `Documents/Fotostudio Pro/Tutorials` and `Samples`, `AccountPictures`, `Desktop` with shortcuts; `max/scoop/apps/7zip` | `system` by rule, no model call; the files have no category |
| Archive known by its entries (version 4) | `max/Documents/20190312.zip` with bank and card statements | `finance`, category open |
| Media | `Musik`, `Videos`, `Screenshots` | `uniform` |
| Placeholder | `Neuer Ordner` (empty) | `empty` |
| Hidden tool folder | `.ssh` | `system` |
| Sensitive dummies | password list, recovery codes, `.kdbx`, `.pfx`, `.env`, private key, bank statement, tax papers, doctor letter, ID scan, private letter | markers |
| Counter-samples (version 2) | a bank statement and a payslip whose names no rule knows, a personal letter laid out like a formal one | `finance`, `private_message`; only the model can mark them |

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
  "corpus": { "name": "small", "generator_version": 3, "seed": 36, "description": "..." },
  "root": "tree",
  "directories": [
    { "path": ".", "verdict": "mixed", "note": "Drive root: folders with different purposes." },
    { "path": "Fotos/2019 Skifahrt", "verdict": "uniform", "note": "Photos of one trip." }
  ],
  "files": [
    { "path": "Desktop/Einkaufsliste.txt", "category": null, "sensitive": [],
      "note": "Category ambiguous (document, review_needed, trash_candidate)." },
    { "path": "Downloads/Kontoauszug_2024_03.pdf", "category": "personal_document", "sensitive": ["finance"],
      "note": "Bank statement: a record about the person, not correspondence (version 2; was null in version 1)." },
    { "path": "Fotos/2019 Skifahrt/IMG_20190212_101530.jpg", "category": "photo", "sensitive": [] }
  ]
}
```

- Paths are relative to `root`, with forward slashes; the scan root is `.`. Every folder and
  file on disk is listed exactly once; the reader refuses duplicates, unknown codes, a missing
  `sensitive` list and entries whose folder is not listed. Paths the scan finds but the truth
  does not list are reported as `untracked`.
- `verdict` and `sensitive` use the codes of [glossary.md](glossary.md), the same the database and
  the CLI JSON carry; `category` is the category name in `snake_case` as in all CLI JSON
  (`document_scan`). They are compared through the enums (`EvalCodes`), never as strings. (Until
  12.11.2026 the stored codes were German and `EvalCodes` bridged between the two; schema step 13
  also rewrote the expected and actual values of stored reports.)
- The truth must be unambiguous. `category: null` means no single category is right (a shopping
  list is `document`, `review_needed` or `trash_candidate`) and only the markers are measured; files
  inside a unit, and the noise of a dump (app data, caches), have no category, because they are not
  classified file by file. `sensitive: []`
  means the file must not be marked. `note` says why, for people.

The same file shape serves as an answer sheet for `--stub`: the truth itself answers perfectly,
a copy with changed entries answers wrong on purpose. Where the truth leaves a category open, the
stub answers `document`, because a model has to commit to one as well.

## Expected findings

`sfo eval generate` also writes `<dir>/findings.json` (`SmallCorpusFindings`, Infrastructure/Evaluation): every
finding the corpus should give, and no other. It is kept apart from `truth.json`, so the truth of verdicts and
categories and the truth of findings change on their own. When the file is there, `sfo eval` also runs the steps
the findings need and that call no model (`sfo compare` without a library, `sfo bundles build`) and measures the kind
`finding`; without it, findings are not measured. No library is compared, so `not_backed_up` is never expected.

```json
{ "schema_version": 1,
  "findings": [
    { "kind": "in_noise", "path": "Downloads", "group": "images", "note": "A camera photo in the download folder." },
    { "kind": "sensitive", "path": "Dokumente/Bank", "markers": ["finance"] },
    { "kind": "duplicate", "path": "Programme/Tool", "scope": "folders" }
  ] }
```

`path` is the `relative_path` of the finding; `group`, `scope` and `markers` narrow it when given. An expectation
is a `hit` when a listed finding (open or accepted) has its kind, path and narrowing, `wrong` when a finding lies
at its path with another kind or narrowing, and `missing` otherwise. Every listed finding no expectation takes is
`superfluous`: it is named as a failure and counted on its own (`findings.superfluous`), not in `total`, because
many wrong hints are worse than a missing one. With the scripted answers the small corpus gives 20 of 20 and no
superfluous finding (13.11.2026, https://github.com/crack666/smart_file_organizer/issues/64). Where the truth leaves
a category open, the stub answers by file type since then (`document` for documents, `archive`, `code`, and
`trash_candidate` for logs and settings files); before, it answered `document` for every such file, which made a
log file, a settings file and an archive superfluous findings.

## Truth changes

Every change of the truth is listed here with its reason. The truth follows what is right for the
user, never what the program happens to answer.

### Version 4 (13.11.2026, https://github.com/crack666/smart_file_organizer/issues/67)

Nothing of version 3 changed. New are the cases the first real run of an old profile showed, where the model was
asked about what a rule should decide, or missed what mattered most:

| Path | Verdict or category | Markers | Why |
|---|---|---|---|
| `Profile alter PC/Public` and every folder below | `system` | - | The profile of all users holds only what an application and Windows put there: learning videos and sample pictures of an application in `Documents/Fotostudio Pro`, account pictures, installer shortcuts on the desktop. No dump: it has no `NTUSER.DAT` and no `AppData`. |
| `Profile alter PC/max/scoop` and below | `system` | - | Programs of a package manager. |
| `Profile alter PC/max/Documents/Garantie` | `uniform` | - | A folder with one file (a warranty card, `document`). |
| `Profile alter PC/max/Documents/desktop.ini`, `~$etvertrag_Wohnung.docx` | no category | none | Folder settings of Windows and an Office lock file. |
| `Profile alter PC/max/Documents/20190312.zip` | no category (`archive` or `personal_document`) | `finance` | Bank and card statements packed under a number; only the entry names tell. |

`findings.json` expects one more finding since then: `sensitive` `finance` at `Profile alter PC/max/Documents` (the
zip). The system places, the folder with one file and the shortcuts give no finding. Measured 13.11.2026 with the
perfect stub on master `0221c30` plus this change: `finding` 21/21, 0 superfluous (before: 20/20, 1 superfluous).

The files of the system places have no category, like other noise. A folder linked by a junction and a folder the
scan cannot read are not in the corpus (the generator creates neither); `DirectoryVerdictTests` covers them.

### Version 3 (13.11.2026, https://github.com/crack666/smart_file_organizer/issues/59)

A copy of a phone or a user profile is no unit: nothing in it only makes sense as a whole, and its
photos and papers are what the person wants back. Version 2 said `unit` with everything inside,
which hid the camera photos from the analysis. The new verdict `dump` says what the folder is; its
parts get the verdict they would get anywhere:

| Folder | Version 2 | Version 3 | Why |
|---|---|---|---|
| `Handy-Backup 2021` | `unit` | `dump` | A phone copy, recognised by `DCIM`, `Android`, `LOST.DIR`. |
| `Handy-Backup 2021/DCIM`, `DCIM/Camera` | `unit` | `uniform` | Camera photos of one phone. |
| `Handy-Backup 2021/LOST.DIR` | `unit` | `empty` | It is empty. |
| `Handy-Backup 2021/Android` and below | `unit` | `system` | App data of the phone. |
| the five photos in `DCIM/Camera` | no category | `photo` | They are photos and are analysed now. |

New: `Profile alter PC` with two Windows profiles (the folder of both is `uniform`, a collection of
dumps). `max` is a newer profile (`AppData` with `Local/Temp`, which the scan rules skip, so those two
folders have no verdict in the truth; `Desktop`, `Documents` with `Steuer 2014`, `Downloads`,
`Pictures`), `lisa` an old one (`Anwendungsdaten/Mozilla` with a password store, `Lokale
Einstellungen` with 30 temp and cache files, `Cookies`, `Eigene Dateien` with `Eigene Bilder`). Both
have `NTUSER.DAT`. The documents, desktop and picture folders are `mixed` or `uniform` with real
categories; the noise is `system` and its files have no category. `Cookies` is no noise path of the
patterns, so the model judges it, and the truth says `system`. A credentials file on the desktop of
`max` and the password store of `lisa` must stay marked. The parent folder was first called `Benutzer`,
which next to `Programme` made the corpus root look like a system drive; it was renamed, because the
corpus root is no computer.

### Version 2 (12.11.2026, https://github.com/crack666/smart_file_organizer/issues/50)

The prompt now gives each kind of paper one category
(`PersonalDocument`: "a record about the person that is not correspondence: CV, certificate,
contract (rental, employment, loan), tax papers, bank and pay statements, medical findings and
doctor letters"; `DocumentScan`: "a picture of a paper page …, whatever the paper says"). Files
whose category was left open because the old prompt allowed two answers now have one:

| File | Version 1 | Version 2 | Why |
|---|---|---|---|
| `Downloads/Kontoauszug_2024_03.pdf` | `null` | `personal_document` | A bank statement is a record about the person, not a bill (`invoice`) and not correspondence. |
| `Dokumente/Steuer 2022/Steuererklaerung_2022.pdf` | `null` | `personal_document` | Tax papers are named under `PersonalDocument`. |
| `Dokumente/Steuer 2022/Lohnsteuerbescheinigung_2022.pdf` | `null` | `personal_document` | A tax certificate of the employer is a tax paper. |
| `Dokumente/Gesundheit/Arztbrief_Orthopaedie_2023.pdf` | `null` | `personal_document` | A doctor letter is a medical record about the person; it is not a letter the person wrote or received as correspondence. Before, `letter` was a fair answer too. |
| `Dokumente/Gesundheit/Befund_Blutbild.pdf` | `null` | `personal_document` | A medical finding is named under `PersonalDocument`. |
| `Dokumente/Persoenliches/Personalausweis_Scan.pdf` | `null` | `document_scan` | The file is a PDF of one page image without text; the format decides, not what the paper says. The `identity` marker carries what it is. |

Still open (`null`), because no single category is right: the password list and the shopping list
(`document`, `review_needed` or `trash_candidate`), `alt/Tabelle1.csv` (a bare table of amounts:
`document` or `trash_candidate`), and files without a document type (installer, archive, log,
shortcut, password store, certificate, thumbnail cache).

New files, so that a too narrow boundary of a marker shows (the marker rules of the prompt were
sharpened in the same issue and could now miss real cases):

| File | Category | Markers | Why |
|---|---|---|---|
| `Dokumente/Bank/Umsaetze_Girokonto_2024_05.pdf` | `personal_document` | `finance` | A bank statement whose name no rule knows: must stay `finance`, not be taken for an invoice. |
| `Dokumente/Bank/Verdienstnachweis_2023_05.pdf` | `personal_document` | `finance` | A payslip whose name no rule knows: must stay `finance`. |
| `Desktop/Brief_an_Lisa.txt` | `letter` | `private_message` | A personal letter laid out like a formal one (addresses on top): must stay `private_message`. |

A test checks that no name rule marks the three new files, so they measure the model
(`Generator_CounterSamples_AreLeftToTheModel`). The new folder `Dokumente/Bank` is `uniform`.

Unchanged on purpose: the invoices, the rental contract and the formal letters keep `sensitive: []`
although every model in the run of 12.11.2026 marked them (see [Measured](#measured)); that was the
prompt, which was wrong. `.ssh/id_ed25519.pub` keeps `sensitive: []`, see the next section.

## What is measured

| Kind | Items | Result compared |
|---|---|---|
| `directory_verdict` | every truth folder with a verdict | stored verdict, whether a rule, the model or the sample check decided |
| `file_category` | every truth file with a category | the model's category, or the one inherited from a confirmed sample (a human override is ignored) |
| `finding` | every expected finding of `findings.json` | the listed findings of the job; findings nobody expects count as `superfluous`, see [Expected findings](#expected-findings) |
| `sensitive` | every truth file | markers of the rules and of the model together; a hit needs exactly the expected set. A file inside a folder the scan skipped and marked (`.ssh`) carries the markers of that folder, see [below](#files-in-a-skipped-marked-folder) |

Each item is a `hit`, `wrong` (a different result) or `missing` (not scanned, not analysed, or a
model error). For `sensitive` the score also counts files the truth marks, how many of them carry
every expected marker, how many lack one, and false alarms on unmarked files.

A failure names the item, the expectation, the result, who decided and why:

```text
directory_verdict wrong   Handy-Backup 2021/LOST.DIR: expected empty, got unit  [rule inside-unit]  Part of the unit 'Handy-Backup 2021'; a unit is handled only as a whole.
directory_verdict wrong   Fotos/Sommerfest 2022: expected uniform, got mixed  [sample]  raised from uniform (model gemma4:26b): 5 of 6 sample files are Photo, 1 deviate, ...
file_category     wrong   Downloads/IMG_20230812_143012.jpg: expected photo, got screenshot  [model gemma4:26b]
sensitive         wrong   Desktop/wiederherstellungscodes.txt: expected credentials, got none  missed credentials
```

`[sample]` means the staged analysis overruled the pre-assessment: the sample of a uniform
folder contradicted it (or the model found a marker the rules had missed), so the folder became
mixed. For a file it means the category was inherited from a confirmed sample.

The `key` of a failure (`file_category:Fotos/...jpg`) is stable across runs and is what
`--compare` matches on.

### Files in a skipped, marked folder

The scan does not enter dot folders, so the files in `.ssh` are never listed. Since
https://github.com/crack666/smart_file_organizer/pull/52 the skipped folder itself carries the
marker (`keys`, rule `dir:ssh`), and everything below it is withheld with the folder.

Decided 12.11.2026 (https://github.com/crack666/smart_file_organizer/issues/50): the truth stays
per file, and the evaluation reads the marker of the nearest skipped, marked folder as the result
for a file the scan did not list. The alternative, marking the folder in the truth instead of its
files, was rejected: the truth would then say what the program does, not what is right. Per file,
the private key is `keys` and the public key is not secret. So:

- `.ssh/id_ed25519` counts as found (before: missing).
- `.ssh/id_ed25519.pub` counts as a false alarm `[rule dir:ssh]`. This failure stays on purpose: the
  folder rule withholds a file that is not secret. It is cheap (the user loses nothing by
  skipping a public key) and much safer than entering `.ssh`, so the rule is right, and the
  failure shows the price.

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
path, the codes and the outcome. Further correction kinds (a verdict overridden) are added as new
`EvalCaseKind` codes with their own runner once the product has those corrections.

### Findings reported wrong

`sfo findings wrong <job> <key>` (https://github.com/crack666/smart_file_organizer/issues/26, rules in
[architecture.md, Findings flow](architecture.md#findings-flow)) stores a case of kind `finding`, origin
`finding_reported_wrong`: `subject_path` is the folder of the finding (or the job root) plus `#` and the finding
key, `observed` the kind code, `expected` `none`, `evidence_json` the key, kind, certainty, reason, file count,
examples, action and note, as JSON. A sensitive finding keeps only its markers, no evidence. Reporting the same
finding again updates the case. These cases are rule results, not model answers: `sfo eval --cases` leaves them
out. They are the material of the measurement series of findings
(https://github.com/crack666/smart_file_organizer/issues/10): how often each kind is wrong, and why.

## Runs and comparison

Every `sfo eval` run is stored in `eval_runs`: mode (`corpus` or `cases`), subject
(`small v1 seed 36` or `cases`), start and end, job, model or stub, prompt fingerprint, app
version (with the commit when the build knows it), the settings and thresholds of the run
(`ollama.max_parallel_requests`, `ollama.max_samples_per_directory`, upload limits, timeout,
summary language, scan rules with hash, and for corpus runs since 12.11.2026 the sample thresholds
`sampling.sample_size`, `sampling.max_deviation_share`, `sampling.small_directory_max_files`) and the
full report.

The prompt fingerprint (`OllamaService.PromptFingerprint`) is a hash over the system prompt, the
summary language, the messages built for a fixed sample (system and user message of classification with and without
the keep hint, pre-assessment, summary) and their answer schemas. Two runs with the same fingerprint asked the model the same way.
On 13.11.2026 it changed from `d2f30ce0d015d9d5` to `07982d6fb2feeae6`: the fixed part of each phase moved to the
system message, the answer lost `suggested_target`, `keep_reason` is asked for `temporary` only and `summary` is capped.
`sfo eval` with `--stub` scored the same before and after (small v3 seed 36: verdicts 60/60, categories 78/78,
sensitive 159/160).

`sfo eval --compare` without ids compares the newest run with the newest earlier run of the same
mode and subject; with one id, that run with the one before it; with two ids, those two. It lists
what changed (model, prompts, version, each setting), the score per kind before and after, the
items fixed and the new failures.

## Measured

### Scripted answers after issue 67 (13.11.2026)

_Scripted answers (`--stub truth.json`), on the branch of https://github.com/crack666/smart_file_organizer/issues/67
rebased on master `0630264`, prompt fingerprint `d2f30ce0d015d9d5`, sampling 15 / 10 % / small folders up to 3 files.
After the rebase on master `b88e089` (new prompt fingerprint `07982d6fb2feeae6`) the v4 run gave the same scores and
the same 26 + 92 model calls.
Model calls are the directory verdicts with source `model` plus the file results with origin `model` (summaries not
counted):_

| Corpus | Build | `directory_verdict` | `file_category` | `sensitive` | Model calls |
|---|---|---|---|---|---|
| small v3 seed 36 | before (master `f58d2c9`) | 60/60 | 78/78 | 159/160 | 27 + 91 = 118 |
| small v3 seed 36 | after | 60/60 | 78/78 | 159/160 | 26 + 90 = 116 |
| small v4 seed 36 | before (master `0630264`) | 71/71 | 79/79 | 173/174 (20 marked, 20 found, 1 false alarm) | 33 + 95 = 128 |
| small v4 seed 36 | after | 71/71 | 79/79 | 173/174 (20 marked, 20 found, 1 false alarm) | 26 + 92 = 118 |

`sfo eval --compare` of the two v4 runs lists no fixed and no new failure; the remaining one is the deliberate false
alarm of `.ssh/id_ed25519.pub`. The perfect stub cannot show what the rules fix, because it answers the truth wherever
it is asked (it calls the public folders `system` and marks the zip `finance` itself). What the runs show is that the
rules decide the new cases without a failure and with ten model calls fewer on version 4: seven directory verdicts
(the public profile and its places, `scoop`, the folder with one file) and three files (`desktop.ini`, `ntuser.ini`,
the lock file). The zip carries `finance` from the rules `entry:word:kontoauszug;entry:word:kreditkarte` and is stored
as worth keeping. Tests show the same without the stub: an archive with bank statements inside is `finance` by rule
(`Phase2EvidenceTests`), and the model is not called for system places, links, unreadable folders, folders with one
file and system files (`DirectoryVerdictTests`, `Phase2EvidenceTests`). A run with real models on version 4 has not
been made (no GPU in this package).

### Scripted answers after issue 59 (13.11.2026)

_Scripted answers (`--stub truth.json`), on the branch of
https://github.com/crack666/smart_file_organizer/issues/59, prompt fingerprint `d2f30ce0d015d9d5`
(unchanged: `dump` is a rule-only verdict and not in the answer schema), sampling 15 / 10 % / small
folders up to 3 files:_

| Corpus | `directory_verdict` | `file_category` | `sensitive` |
|---|---|---|---|
| small v3 seed 36, before (rules of issue 50) | 35/60 | 58/78 | 158/160 (19 marked, 18 found, 1 false alarm) |
| small v3 seed 36, after | 60/60 | 78/78 | 159/160 (19 marked, 19 found, 1 false alarm) |

`sfo eval --compare` lists 46 fixed failures and no new one. Before, all 25 folders of the phone
dump and the two profiles were `unit` (the dumps by rule `dump`, everything else `inside-unit`), so
none of the 20 photos and papers in them was classified, and the tax notice in `max/Documents/Steuer
2014` missed its `finance` marker, which only the analysis gives. After, the one remaining failure is
the deliberate false alarm of `.ssh/id_ed25519.pub`. The two credentials files in the profiles were
found before and after: the name rules mark them whether their folder is analysed or skipped.

### Scripted answers after issue 50 (12.11.2026)

_Scripted answers (`--stub truth.json`), on the branch of
https://github.com/crack666/smart_file_organizer/issues/50, prompt fingerprint `d2f30ce0d015d9d5`,
sampling 15 / 10 % / small folders up to 3 files:_

| Corpus | `directory_verdict` | `file_category` | `sensitive` |
|---|---|---|---|
| small v1 seed 36, before (master) | 32/41 | 49/49 | 96/97 |
| small v1 seed 36, after | 41/41 | 49/49 | 96/97 |
| small v2 seed 36, after | 42/42 | 58/58 | 99/100 (16 marked, 16 found, 1 false alarm) |

`sfo eval --compare` of the v1 runs lists 10 fixed failures and no new one: the six folders of the
phone dump (now one unit by the shared dump recognition), `Dokumente/Briefe`,
`Dokumente/Persoenliches` and `Fotos/Sommerfest 2022` (sample check), and `.ssh/id_ed25519`;
`.ssh/id_ed25519.pub` is new as the deliberate false alarm described above. On both versions the
one remaining failure is that false alarm. How the issues were fixed:

- The phone dump: a folder that `bundle-patterns.json` recognises as a copy of a device, a computer
  or software is a `unit` by rule `dump`, with everything inside. Bundles, library review and the
  verdict now share one recognition. The scan root is never a dump. (Changed by issue 59 on
  13.11.2026: a copy of a device or profile is `dump`, its parts are judged, and the scan root can be one.)
- The sample check counts by purpose: a video next to photos, and invoice, letter, personal
  document, document scan next to document, are no deviation. At least one deviation is
  tolerated in every folder (5 photos and 1 video stay `uniform`). Folders of at most 3 files keep
  the verdict of the pre-assessment and are judged file by file. A marker the model finds marks
  that file only; the folder keeps its verdict and nothing in it inherits a result.
- The `.ssh` key: [Files in a skipped, marked folder](#files-in-a-skipped-marked-folder).

### Real models on version 1 (12.11.2026)

_Measured 12.11.2026 21:11 to 21:32 local time by the owner, on master `84e9138`, corpus small v1
seed 36, prompt fingerprint `66eb5ea2b5813d77`, directly against Ollama, one request at a time, one
run each; times include the first load of the model
(https://github.com/crack666/smart_file_organizer/issues/50)._

| Model | Analysis | `directory_verdict` | `file_category` | `sensitive` false alarms | missed | Second checks |
|---|---|---|---|---|---|---|
| perfect stub | 3 s | 32/41 | 49/49 | 0 | 1 | - |
| gemma4:26b | 146 s | 29/41 | 49/49 | 10 | 1 | 1 |
| qwen3.8:27b, 8k context | 232 s | 30/41 | 49/49 | 10 | 1 | 3 or 4 |
| qwen3.8:27b, 256k context | 300 s | 29/41 | 47/49 | 11 | 1 | 3 or 4 |

- All three runs raise the same ten false alarms on the same files: the five invoices and
  `alt/Tabelle1.csv` as `finance`, the rental contract as `finance`, the two cancellations and the
  objection as `private_message`. (The comment on the issue says "seven invoices"; the corpus has
  five, and only 5 + 1 + 1 + 3 gives the ten.) Since all models agree, the cause was the prompt.
  The boundaries of `finance` and `private_message` are now drawn in the prompt and in the
  [glossary](glossary.md#where-finance-and-private_message-end), with examples on both sides.
- The one missed marker is the key in `.ssh`, which the scan never lists; it now counts as found.
- Three new verdict failures against the stub: `Dokumente/Rechnungen 2023` was raised because the
  model reported a marker in the sample; `Dokumente/Gesundheit` (two files, `document` and
  `personal_document`) had no majority; `Handy-Backup 2021/DCIM/Camera` was `uniform` instead of
  part of the dump. All three are covered by the changes above.
- gemma4:26b was fastest at one request at a time (about 1.6 s per file against 2.5 s and 3.3 s).
  Differences of one or two items lie within the noise of one run; the spread between runs of one
  model is not measured. The two qwen runs also differ in temperature (0.2 in the 8k tag, the
  default in the 256k tag), because `sfo` sets neither temperature nor context size
  (https://github.com/crack666/smart_file_organizer/issues/53).

A run with real models after issue 50 has not been made yet (the package had no GPU). To compare
with the table above, run the new build on the existing version 1 folder (its `truth.json` still
says v1, so `--compare` matches the runs of 12.11.2026), then on a fresh version 2 folder for the
counter-samples:

```bash
sfo eval <v1-folder> --db <db-of-12.11> --model gemma4:26b && sfo eval --compare --db <db-of-12.11>
sfo eval generate <v2-folder> && sfo eval <v2-folder> --db <db-of-12.11> --model gemma4:26b
```

What to look at: the ten false alarms should be gone, and the three counter-samples
(`Dokumente/Bank/*`, `Desktop/Brief_an_Lisa.txt`) must still be found.

### Scripted answers before issue 50 (12.11.2026)

_12.11.2026, scripted answers (`--stub truth.json`), corpus small v1 seed 36, on the branch of
issue 36 rebased on the staged analysis:_ `directory_verdict` 32/41, `file_category` 49/49,
`sensitive` 96/97. (Before the rebase on the staged analysis the same run gave 35/41 verdicts;
the three folders below under "sample check" were hits then. The switch to English codes in
https://github.com/crack666/smart_file_organizer/issues/35 changed nothing: same scores, `--compare`
against the run on master before it found no new and no fixed failure, measured 12.11.2026.) With perfect model answers every
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
