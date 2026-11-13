# Glossary of codes

Every fixed value that Smart File Organizer stores in its database, writes to JSON, accepts on the
command line or asks the model for is an English code from this list. The table of codes was
decided in https://github.com/crack666/smart_file_organizer/issues/35.

- Codes are lower case, words joined by `_`. They never change with the display language.
- People see words instead of codes: the GUI and the text output of the CLI translate them through
  `CodeLabels` (`src/SmartFileOrganizer.Domain/Enums/CodeLabels.cs`), in English or German depending
  on the summary language in the settings. JSON always carries the code.
- In code, a code belongs to an enum. The heading of each table below names it. `GlossaryTests`
  fails when an enum value has no code or no entry here, so a new value cannot be added silently.
- Before https://github.com/crack666/smart_file_organizer/issues/35 the first groups below were stored
  as German words (`einheit`, `gemischt`, `zugangsdaten`, ...). Schema step 13 rewrites existing
  databases; a model answer that still uses a German word is read, and only the code is stored.

## Codes of the decision table

### Directory verdict (`DirectoryVerdictKind`)

| Code | Meaning |
|---|---|
| `unit` | A project, program, game or backup that only makes sense as a whole; never split up file by file. |
| `uniform` | A collection of similar content with one purpose, such as the photos of one trip or the invoices of one year. |
| `mixed` | Storage without a common purpose (desktop, downloads, misc); its files are handled one by one. |
| `empty` | Contains no files, neither directly nor in subdirectories. |
| `system` | An operating system, tool or cache directory that is not user content. |
| `dump` | A copy of a phone, a computer, a user profile or a removable drive. Recognised as a whole, but not a unit: its worth-keeping parts get verdicts of their own and are analysed, only its noise is skipped. Decided by a rule only; the model is never offered it (https://github.com/crack666/smart_file_organizer/issues/59). |

### Retention value (`RetentionValue`)

| Code | Meaning |
|---|---|
| `keep` | Personal or hard to replace. |
| `review` | Unclear; a person should look. |
| `disposable` | Can be recreated or downloaded again (installers, caches, temporary files). |

### Origin of a directory verdict (`VerdictSource`)

| Code | Meaning |
|---|---|
| `rule` | A deterministic rule decided without calling the model. |
| `model` | The model decided, or was asked and failed. |

### Origin of a file result (`ResultOrigin`)

| Code | Meaning |
|---|---|
| `rule` | A deterministic rule decided. |
| `model` | The local model judged the file itself, on its own or as a sample member. |
| `inherited` | The file was not looked at; category and target were taken over from the confirmed sample of its directory. |
| `human` | A person set the result. |
| `agent` | A steering agent set the result through the command line. |
| `external` | The result was imported from another tool. |

### Sensitive markers (`SensitiveMarker`)

A file can carry several markers; they are stored as a comma-separated list in this order.

| Code | Meaning |
|---|---|
| `credentials` | Passwords, password stores, login data, recovery codes, API keys or tokens, `.env` files. |
| `keys` | Private keys, certificates with a private key, crypto wallets or seed phrases. |
| `private_message` | Personal messages between people who know each other: personal letters, chats and chat exports, private e-mails, mail archives and chat databases. Not a formal letter to or from a company, an authority or a landlord, a newsletter or an order confirmation. |
| `identity` | Identity cards, passports, driving licences or scans of them. |
| `finance` | Papers that show the money, income or debts of the person: bank and credit card statements, tax returns and tax certificates, payslips, depot and loan papers, card numbers. Not an ordinary invoice or receipt, a rental or other contract, or a table of amounts without account, income or debt. |
| `health` | Medical findings, doctor letters, health insurance documents. |

Rule ids (`sensitive_rule`) name the rule that set a marker, never the content: `name:<file name>`, `ext:<extension>`,
`path:<fragment>`, `word:<keyword>`, `header:<kind>`, `dir:<folder rule>`. The prefix `entry:` (`entry:word:kontoauszug`)
means the rule fired on the name of an entry inside an archive, read from its listing without unpacking
(https://github.com/crack666/smart_file_organizer/issues/67); the archive carries the marker with origin rule.

#### Where `finance` and `private_message` end

A marker withholds the content of a file from everything that leaves the machine and sends the file to
review. Both mistakes cost something: a false alarm costs rework and makes a folder of ordinary papers
look sensitive, a missed marker can let private content out. The line below was drawn on 12.11.2026
after three runs with real models marked the same ordinary invoices and a rental contract as `finance`
and the same formal letters as `private_message`
(https://github.com/crack666/smart_file_organizer/issues/50). The prompt of the model says the same in
the same words (`OllamaService.BuildClassificationPrompt`). If you disagree with a line, change it here
and in the prompt, and the corpus of [evaluation.md](evaluation.md) shows what the change costs.

`finance` asks: does the paper show what the person has, earns or owes?

| `finance` | not `finance` |
|---|---|
| a bank or credit card statement | an invoice for electricity, internet, a craftsman or a shop |
| a tax return, a wage tax certificate | a receipt or an order confirmation |
| a payslip | a rental contract (it names a rent, not the person's money) |
| a depot statement, a loan or credit contract | a price list, a household table of amounts without account or income |

`private_message` asks: is it a message between people who know each other?

| `private_message` | not `private_message` |
|---|---|
| a personal letter to a grandmother or a friend | a cancellation of a phone contract or a gym membership |
| a chat export, a chat database (`msgstore.db`) | an objection to a decision of an authority |
| a private e-mail, a mail archive (`.pst`, `.mbox`) | an application, a complaint, a letter to the landlord |
| | a newsletter, an order confirmation |

The name and path rules of the scan (`SensitiveFileRules`: `kontoauszug`, `lohnsteuer`, `.pst`, ...) mark
independently of the model and are not changed by this line.

### File category (`FileCategory`)

What a file is. The category says what kind of thing it is, not how important or how private it is;
that is the importance and the sensitive markers. The JSON of the CLI and the truth of the evaluation
corpus write the enum name in `snake_case`; the model is asked with the enum name (`PersonalDocument`).
Each kind of paper has one category (decided 12.11.2026,
https://github.com/crack666/smart_file_organizer/issues/50; before, the prompt offered "PersonalDocument
or Letter" and filed invoices next to bank statements).

| Code | Meaning |
|---|---|
| `unknown` | Not determined: no result, or a binary nothing can read. |
| `photo` | A photo taken with a camera or phone. |
| `document_scan` | A picture of a paper page: a scan, or a PDF of page images without text, whatever the paper says. |
| `screenshot` | A screen capture. |
| `video` | A video. |
| `audio` | Music or a sound recording. |
| `document` | Any other text that is none of the kinds below: a manual, an article, notes, a list, a table, a form. |
| `invoice` | A bill or receipt for goods or services: electricity, internet, a craftsman, a shop, a garage. |
| `personal_document` | A record about the person that is not correspondence: CV, certificate, contract (rental, employment, loan), tax papers, bank and pay statements, medical findings and doctor letters. |
| `letter` | Correspondence written to or from someone: a cancellation, an objection, an application, a complaint, a personal letter. |
| `archive` | A ZIP, RAR, 7z or TAR archive, judged by its listing. |
| `code` | Source code. |
| `software_installer` | An installer, setup or update package, or a program. |
| `system_file` | A library, font, driver or other file of the system or of a program. |
| `trash_candidate` | A temporary file, log, crash dump or cache that can go. |
| `review_needed` | Personal or made by the user, but unclear what it is: a person should look. |

For the sample of a uniform folder, related categories count as one purpose: `photo` and `video` (the
pictures and clips of one occasion), and `document`, `invoice`, `letter`, `personal_document` and
`document_scan` (kinds of documents). A folder with photos and one clip of the same party, or with a CV,
a contract and a scanned ID card, stays `uniform` ([architecture.md](architecture.md), Resolve).

### Keep hint for files in mixed directories (`KeepHint`)

| Code | Meaning |
|---|---|
| `temporary` | An installer, download remainder, duplicate or other file that can be recreated. |
| `worth_keeping` | Personal or hard to replace. |

### Role of a scan (`ScanRole`)

| Code | Meaning |
|---|---|
| `source` | A medium to be cleaned up (the default). |
| `library` | The organised archive of the person: only read, never changed, never analysed by a model. |

### Comparison with the library (`ContentState`)

| Code | Meaning |
|---|---|
| `backed_up` | A file with exactly the same content (same SHA-256) is in the library. |
| `new` | No library file has this content, and none has the same name in a folder of the same name. |
| `differs` | Not in the library by content, but a file with the same name lies in a folder of the same name there. |
| `error` | The content could not be determined (read error, file gone, library unreachable); never counts as `new` or `backed_up`. |

`file_nodes.analysis_skip` and `directory_nodes.analysis_skip` carry `backed_up` from this list when
`sfo analyze --against` left the file or folder out because the library holds it; no other value is stored there.

### Reserved for planned work

These codes are fixed by the decision table but no code uses them yet.

| Group | Codes |
|---|---|
| State of a plan action | `proposed`, `approved`, `rejected`, `executed`, `failed`, `undone` |

## Scan

Links and read errors: https://github.com/crack666/smart_file_organizer/issues/57 and
[scan-rules.md](scan-rules.md#links-and-read-errors).

### What the scan did with a folder (`DirectoryStatus`)

| Code | Meaning |
|---|---|
| `pending` | Found but not yet decided (only during a scan). |
| `scanned` | Entered: its files are listed and its subfolders decided one by one. |
| `skip` | Not entered by a rule; its files are not listed. |
| `shallow` | Its files are listed, its subfolders counted but not entered. |
| `error` | It could not be read; the cause is stored with it. Never means empty. |
| `link` | A link that was not followed; kind and target are stored with it, its content is not counted. |

### Kind of a link (`LinkKind`)

| Code | Meaning |
|---|---|
| `junction` | A Windows directory junction, such as `All Users` or `Documents and Settings` in an old profile. |
| `symlink` | A symbolic link to a file or a folder, on Windows or Unix. |
| `mount_point` | A Windows folder that shows another volume. |

### Cause of a read error (`ScanErrorCause`)

| Code | Meaning |
|---|---|
| `access_denied` | The account running the scan may not read the folder or file. |
| `not_found` | The folder or file was gone when the scan came to read it. |
| `path_too_long` | The path is longer than the system accepts. |
| `io_error` | Any other read error of the file system or the device. |

### Are two paths the same file (`SameFileAnswer`)

| Code | Meaning |
|---|---|
| `same` | Both paths lead to one file: the same path, a link, a hard link, or one volume reached twice. |
| `different` | Both files were found and the file system names them as two files. |
| `unknown` | It could not be decided (a path missing or unreadable, no file identity); never treated as `different`. |

## Staged analysis

### State of a directory sample (`SampleState`)

| Code | Meaning |
|---|---|
| `open` | The sample is drawn; the other files of the directory wait for its result. |
| `confirmed` | The verdict stands: the sample agreed, or the directory has so few files (three by default) that each was judged on its own. The other files inherited the result of the sample, unless the model found a sensitive marker the rules had missed; then each of them was judged on its own. |
| `escalated` | The sample disagreed; the directory is now `mixed` and every file is judged on its own. |

### Why a file is in a sample (`SampleRole`)

| Code | Meaning |
|---|---|
| `stratum` | Drawn from its extension group, in proportion to the group size. |
| `size` | The smallest or largest file, or far away from the median size. |
| `date` | The oldest or newest file. |
| `anomaly` | Named as anomalous by the directory pre-assessment. |
| `sensitive` | Marked as sensitive; such a file never inherits and is always judged on its own. |

## Certainty and second checks

### Certainty of a file result (`ResultCertainty`)

| Code | Meaning |
|---|---|
| `certain` | Two independent signals agree, or an unambiguous rule decided. |
| `likely` | One answer that nothing contradicts, an inherited result of a confirmed sample, or a rule category set against the model. |
| `uncertain` | A second check was due and did not happen, or a rule decided without being sure. |
| `review` | Two answers disagree, or the model could not say what the file is: a person should look. |

### Reason for a second check (`CheckReason`)

| Code | Meaning |
|---|---|
| `missing_confidence` | The model gave no confidence. |
| `low_confidence` | The confidence of the model is below the configured limit. |
| `unclear_category` | The model answered ReviewNeeded or Unknown. |
| `kind_mismatch` | The category does not fit the kind of file the first bytes or the extension name. |
| `rule_disagrees` | An unambiguous metadata rule contradicts the category, or the kind of file does not allow it (a video, audio or code file given another category: rule note `type_limit`); the rule wins without a second call. |
| `verdict_disagrees` | The file contradicts the retention value of its directory. |

### Outcome of a second check (`CheckOutcome`)

| Code | Meaning |
|---|---|
| `agreed` | The second answer agrees with the first; the result is `certain`. |
| `disagreed` | The answers disagree; the result is `review`. |
| `corrected` | The first answer broke a rule about the kind of file and the second one fits it; the second stands. |
| `rule_wins` | An unambiguous rule set the category against the model; no second call. |
| `failed` | The second call failed; the first answer stays as `uncertain`. |
| `skipped` | A second check was due but not made (switched off, or a person corrected the file). |

### Kind of a model call (`ModelCallKinds`)

Counted per run in `model_calls` of `sfo analyze --json` and `sfo eval --json`.

| Code | Meaning |
|---|---|
| `pre_assessment` | Phase 1: one call per directory, verdict and sample plan. |
| `classification` | Phase 2: the first call for a file. |
| `second_check` | Phase 2: the second call for an unsure or contradicted file, with its directory as context. |
| `summary` | Phase 3: one call per directory, the closing summary. |

## File facts

### Fact key (`FileFactKey`)

| Code | Meaning |
|---|---|
| `detected_format` | Format recognised from the first bytes (`jpeg`, `pdf`, `zip`, `pe`, ...). |
| `width` | Width in pixels of an image or video. |
| `height` | Height in pixels of an image or video. |
| `taken_at` | When a photo or video was taken, from its metadata. |
| `device` | Camera or recording device (make and model) from the metadata. |
| `has_location` | `true` when the metadata contain a location; the coordinates are never stored. |
| `image_origin` | Where an image comes from, as an image origin code. |
| `page_count` | Number of pages of a document. |
| `has_text` | `true` when a PDF carries extractable text, `false` for a scanned PDF. |
| `title` | Document or track title from the file properties or tags. |
| `author` | Document author from the file properties. |
| `created_at` | Creation date from the document properties. |
| `duration_seconds` | Playing time of audio or video in whole seconds. |
| `artist` | Artist from the audio tags. |
| `album` | Album from the audio tags. |
| `audio_kind` | Speech recording or music, as an audio kind code. |
| `entry_count` | Number of file entries listed in an archive. |
| `unpacked_bytes` | Sum of the uncompressed sizes of the listed archive entries. |
| `archive_content` | What an archive mostly holds, as an archive content code. |
| `encrypted` | `true` when an archive or document is password protected. |
| `scene` | What a picture shows, in a few words of the summary language (`beach with sea`); source `model`. Free text, at most 40 characters. |
| `activity` | What the people in a picture do, in a few words (`skiing`); source `model`. Free text, at most 40 characters. |
| `setting` | Indoors or outdoors, as a scene setting code; source `model`. |
| `season` | The season a picture shows, as a season code; source `model`. Only when the picture shows it, never from a date. |
| `occasion` | The kind of occasion of a picture, as an occasion code; source `model`. |
| `people` | `true` when a person is visible in a picture, `false` when none is; source `model`. |
| `document_kind` | The kind of a document, as a document kind code; source `model`. |
| `issuer` | Who issued a document (company, authority, person) as written on it; source `model`. Free text, at most 60 characters. |
| `year` | The year a document is dated, read from its content; source `model`. |

The last nine are the description of stage 1 (https://github.com/crack666/smart_file_organizer/issues/37): the
model gives them in the same call as the category, only for pictures it sees (`scene` to `people`) and for
documents (`document_kind` to `year`). An answer `unknown`, an empty text or a year 0 is not stored. A file marked
sensitive is not described: no description is asked for a file the rules marked, and none is stored for a file
the model marks, so the marker stays the only statement about its content. Free-text facts (`title`, `author`,
`artist`, `album`, `scene`, `activity`, `issuer`) are never stored for a marked file.

### Source of a fact (`FactSource`)

| Code | Meaning |
|---|---|
| `metadata` | Read directly from the file: metadata, tags, properties, page count, archive listing, first bytes. |
| `rule` | Derived by a deterministic rule from metadata, name and path. |
| `model` | Reported by the local model. |

### Image origin (`ImageOrigin`)

| Code | Meaning |
|---|---|
| `camera` | Taken with a camera or phone. |
| `screenshot` | A screen capture. |
| `scan` | A scanned page. |

### Audio kind (`AudioKind`)

| Code | Meaning |
|---|---|
| `speech` | A speech recording such as a voice memo or an interview. |
| `music` | Music. |

### Archive content (`ArchiveContent`)

| Code | Meaning |
|---|---|
| `photos` | At least 60 % of the entries are images. |
| `documents` | At least 60 % are documents (PDF, Office, text). |
| `media` | At least 60 % are audio or video files. |
| `software` | Contains a setup program, an installer package, or programs with their libraries. |
| `code` | At least 60 % are source code, or the listing has a project marker. |
| `mixed` | None of the above dominates. |

### Scene setting (`SceneSetting`)

| Code | Meaning |
|---|---|
| `indoor` | Taken inside a building or a vehicle. |
| `outdoor` | Taken outside. |

### Season (`Season`)

Only what the picture shows (snow, blossom, autumn leaves, summer light), never a guess from the date.

| Code | Meaning |
|---|---|
| `spring` | Spring. |
| `summer` | Summer. |
| `autumn` | Autumn. |
| `winter` | Winter. |

### Occasion (`OccasionKind`)

| Code | Meaning |
|---|---|
| `holiday` | A journey or holiday away from home. |
| `celebration` | A birthday, wedding, party, family feast or festival. |
| `outing` | A day trip or excursion: a hike, a zoo, a city walk. |
| `sport` | Doing or watching sport. |
| `everyday` | Everyday life at home or around it. |
| `work` | Work, school or study. |
| `none` | No occasion: a product photo, a picture of a thing, a meme, a picture of a document. |

### Document kind (`DocumentKind`)

Finer than the category: the category says invoice, letter or personal document, the kind says which paper it is.

| Code | Meaning |
|---|---|
| `invoice` | A bill for goods or services. |
| `receipt` | A receipt, a till slip, an order or payment confirmation. |
| `contract` | A contract: rental, employment, loan, phone, insurance policy terms. |
| `statement` | An account statement: bank, credit card, depot, a yearly statement of a utility. |
| `tax` | A tax return, a tax assessment or a tax certificate. |
| `payslip` | A payslip or a wage statement. |
| `certificate` | A certificate, a diploma, a reference, a birth or marriage certificate. |
| `insurance` | A paper of an insurance other than its contract: a claim, a confirmation. |
| `medical` | A medical finding, a doctor letter, a prescription. |
| `official` | A notice or decision of an authority. |
| `correspondence` | A letter or message to or from someone that is none of the above. |
| `manual` | A manual, a data sheet, instructions. |
| `form` | A form, filled in or blank. |
| `notes` | Notes, lists, drafts of the person. |
| `other` | Any other document. |

## Library review

### Kind of a library finding (`LibraryFindingKind`)

| Code | Meaning |
|---|---|
| `dump` | A device, computer or program dump inside the library. |
| `naming_inconsistent` | Sibling folders are named after different schemes. |
| `variant_spellings` | The same level is spelled in several ways ("Season 1", "S01"). |
| `flat_among_structured` | Folders hold many files flat where most of their siblings are divided into subfolders. |
| `loose_files` | Loose files at the top of the library, next to folders. |
| `same_name_elsewhere` | Folders with the same normalised name at several places. |
| `similar_names` | Nearly equal folder names. |
| `likely_copy` | Subtrees with the same number of files and the same byte total at different places. |
| `empty` | Empty folders or branches, also those holding only system files. |
| `placeholder_name` | Placeholder or catch-all names ("New folder", "misc", "old"). |
| `camera_folder_name` | Folders still named by the camera (`100CANON`). |
| `saved_web_page` | Saved web pages: a `*_files` folder next to the `.htm` file of the same name. |
| `type_mismatch` | The file types do not fit the name of the folder or the area it lies in. |
| `dominant_file` | A single file holds more than half of the bytes of the library. |
| `very_full` | A folder with very many files directly in it. |
| `very_deep` | A branch far deeper than the rest of the library. |
| `sensitive_location` | Credential, key or identity files, reported with path and marker only. |
| `case_inconsistent` | A name starts in another letter case than most of its siblings. |

### Impact of a finding (`FindingImpact`)

| Code | Meaning |
|---|---|
| `high` | Matters most; listed first. |
| `medium` | Worth fixing. |
| `low` | Cosmetic. |

### Kind of dump (`DumpKind`)

| Code | Meaning |
|---|---|
| `device` | Phone, camera or memory card storage. |
| `computer` | A copy of a computer or user profile. |
| `package` | A project, program or unpacked package. |

### Role of a folder (`FolderRole`)

| Code | Meaning |
|---|---|
| `occasion` | One occasion or event. |
| `person` | One person. |
| `device` | One device. |
| `topic` | One topic. |
| `time` | A time span such as a year or a month. |
| `media_title` | A film, series or album. |
| `season` | A season of a series. |
| `document_type` | One type of document, such as invoices. |
| `project` | One project. |
| `collection` | A collection of similar items. |
| `catch_all` | A place for everything that has no other place. |
| `dump` | A copy of a device, computer or package. |
| `mixed` | No single role. |

### Proposed change (`ProposalAction`)

| Code | Meaning |
|---|---|
| `rename` | Give a folder another name. |
| `move` | Move a folder to another place. |
| `merge` | Join folders into one. |
| `split` | Divide a folder into several. |
| `create` | Create a new folder. |

### Origin of advice (`AdviceOrigin`)

| Code | Meaning |
|---|---|
| `rule` | Produced by a deterministic rule. |
| `model` | Produced by the model. |

## Target proposals

`sfo library match` ([library-match.md](library-match.md)). Proposals are data; nothing is moved.

### Placement of a proposal (`MatchPlacement`)

| Code | Meaning |
|---|---|
| `into_existing` | The files go into an existing folder of the library. |
| `as_subfolder` | A named source folder goes, whole and under its own name, into an existing folder next to its kind. |
| `new_folder` | No existing folder fits; a new folder, named in the style of its siblings. |

### Signal behind a proposal (`MatchSignal`)

| Code | Meaning |
|---|---|
| `content_neighbours` | Files with exactly the same content already lie there (from `sfo compare`). |
| `period` | The times of the files fit the period of the folder. |
| `name` | Words of the names agree (folder name, file names, document kind). |
| `file_type` | The folder holds files of the same kind. |
| `siblings` | The folder's subfolders are of the same kind as the source folder. |

### What was matched (`MatchUnitKind`)

| Code | Meaning |
|---|---|
| `file` | A single file. |
| `folder` | A folder of the source. |
| `bundle` | A bundle ([bundles.md](bundles.md)). |

### How the library was chosen (`LibraryChoice`)

| Code | Meaning |
|---|---|
| `option` | Given with `--against`. |
| `comparison` | The library of the source's latest `sfo compare`. |
| `latest` | The newest completed library job; there was no comparison. |

### Whether a library was there (`MatchStatus`)

| Code | Meaning |
|---|---|
| `ok` | A library was found and matched against. |
| `no_library` | No library has been scanned; nothing is proposed. |

## Bundles

### Kind of a bundle (`BundleKind`)

| Code | Meaning |
|---|---|
| `dump` | A copy of a whole device or computer (phone storage, user profile, system drive, USB stick, unpacked software). |
| `dump_keep` | The part of a dump worth keeping: camera folder, documents, chat media, ... |
| `dump_noise` | Everything else of a dump: app data, caches, system folders, empty folders. |
| `folder` | An existing folder with a meaningful name outside dumps; never split, never renamed. |
| `event` | Loose photos and videos taken close together in time: one occasion. |
| `scattered` | Loose photos and videos of one year that formed no group large enough for an event. |

### Origin of a dump (`DumpOrigin`)

| Code | Meaning |
|---|---|
| `phone` | Phone or tablet storage. |
| `computer` | A user profile or a whole system drive of a computer. |
| `removable_drive` | A copied USB stick or memory card. |
| `software` | Unpacked software or a code project. |

### Part of a dump worth keeping (`DumpPart`)

| Code | Meaning |
|---|---|
| `camera` | Photos and videos of the camera. |
| `screenshots` | Screen captures. |
| `chat_media` | Pictures and videos sent in chats. |
| `received` | Files received from others (Bluetooth, sharing). |
| `recordings` | Voice and call recordings. |
| `documents` | Documents. |
| `pictures` | Other pictures. |
| `videos` | Other videos. |
| `music` | Music. |
| `downloads` | Downloads. |
| `desktop` | The desktop of a computer. |

### Time source of a bundle (`TimeSource`)

| Code | Meaning |
|---|---|
| `capture_date` | Capture date from the photo or video metadata. |
| `file_name` | A date and time in the file name. |
| `modified_date` | Modification time from the file system; changes with every copy, so the weakest source. |
| `mixed` | The files of the bundle rest on more than one of these sources. |

## Evaluation

### Measured kind (`EvalKind`)

| Code | Meaning |
|---|---|
| `directory_verdict` | The verdict of a directory. |
| `file_category` | The category of a file. |
| `sensitive` | The sensitive markers of a file. |
| `finding` | The findings of the job against the expected findings of the corpus (`findings.json`). |

### Outcome of a measured item (`EvalOutcome`)

| Code | Meaning |
|---|---|
| `hit` | The result equals the truth. |
| `wrong` | There is a result and it differs from the truth. |
| `missing` | No result: not scanned, not analysed, or the model call failed. |
| `superfluous` | A finding no expectation asks for: noise in the list. Counted apart from `total`. |

### Mode of an evaluation run (`EvalRunMode`)

| Code | Meaning |
|---|---|
| `corpus` | A corpus with a truth file was scanned, analysed and compared. |
| `cases` | The stored test cases from corrections were run again. |

### Kind of a test case (`EvalCaseKind`)

| Code | Meaning |
|---|---|
| `file_category` | A person set another category for a file than the model. |
| `finding` | A finding was reported wrong (`sfo findings wrong`); `observed` is its kind, `expected` is `none`. |

### Origin of a test case (`EvalCaseOrigin`)

| Code | Meaning |
|---|---|
| `review_override` | A manual override in the review. |
| `finding_reported_wrong` | A person or agent reported a finding wrong. |

## Findings

The short list of hints a job ends in (see [findings.md](findings.md)).

### Kind of a finding (`FindingKind`)

| Code | Meaning |
|---|---|
| `not_backed_up` | Files worth keeping whose content is not in the library. Needs a comparison with a library. |
| `in_noise` | Files worth keeping in a place without value: app data of a device copy, a program folder, a folder judged mixed or disposable. |
| `misplaced` | Files whose kind does not fit the purpose of their folder; uncertain at most until targets are checked by content. |
| `sensitive` | Files or skipped folders marked sensitive. Path and marker only, never content; always decided one by one. |
| `duplicate` | Files with identical content (same full SHA-256) at several places of the source. |

### Kind of files of a finding (`group`)

`not_backed_up` and `in_noise` give one finding per place and kind of file (`FindingRules.GroupOf`), so a mixed
folder can be decided kind by kind.

| Code | Meaning |
|---|---|
| `documents` | Office files, PDFs, text. |
| `images` | Pictures and photos. |
| `videos` | Videos. |
| `audio` | Sound files. |
| `archives` | ZIP and other archives. Counted only when a result says they are worth keeping. |
| `installers` | Programs and installers. Counted only when a result says they are worth keeping. |
| `other` | Every other file type. |

### Scope of a duplicate (`duplicates.scope`)

| Code | Meaning |
|---|---|
| `files` | Single files worth keeping, outside every program, project and system folder, exist more than once. |
| `folders` | A whole program, project, system folder or app data exists more than once; one finding for the copied folders. |

### State of a finding (`FindingState`)

| Code | Meaning |
|---|---|
| `open` | Not decided yet. Not stored: no row means open. |
| `accepted` | Taken over; the plan turns it into an action. |
| `rejected` | Not wanted; not listed again. |
| `wrong` | The finding itself is wrong; not listed again and kept as a test case. |

### Kind of a plan action (`PlanActionKind`)

Recommended by every finding, executed by nothing yet.

| Code | Meaning |
|---|---|
| `move` | Copy to the target, verify, then remove the original. |
| `copy` | Copy to the target; the original stays. Never recommended for documents. |
| `quarantine` | Move to a quarantine folder from which it can be restored. |
| `delete_source` | Remove the original, because the same content is already in the library. |
| `leave` | Leave it where it is. Always the recommendation for sensitive files. |

### Content check of a target (`TargetContentCheck`)

| Code | Meaning |
|---|---|
| `not_checked` | Not compared: the target rests on period, names and file types. Every target today. |
| `fits` | The content of the unit fits what the target holds. |
| `contradicts` | The content contradicts the target; the target is dropped. |
| `unclear` | Compared, without a clear answer. |

### Coverage of a kind of finding (`FindingCoverage`)

| Code | Meaning |
|---|---|
| `complete` | Every step the kind needs has run. |
| `partial` | Computed, but a step that sharpens it is missing; the note says which. |
| `not_computed` | Not computed: a step it needs is missing, or there is no library. |

## Model server

How an analysis run tells "the model server is gone" from "this one item fails", and how it waits
(https://github.com/crack666/smart_file_organizer/issues/63). Written to the `wait` lines of
`sfo analyze --progress jsonl`, to `sfo status` and stored in `model_server_waits`.

### Why a call did not reach the model (`ModelServerFailure`)

These are the transient failures: the call is tried again, and after several in a row the run
waits for the server. Any other answer of the server (400, 404, 500, an unreadable reply) is an
error of the one item and is never retried.

| Code | Meaning |
|---|---|
| `connection_failed` | No connection: refused, reset or broken off while sending or reading. |
| `timed_out` | The call ran out of time, and a short probe right after it got no answer either. If the probe answers, the server is up and only this item is slow; that is an error of the item. |
| `busy` | The server answered 429 or 503: it is up but cannot take the call now. Honours `Retry-After`. Never counts against the item. |
| `bad_gateway` | A proxy in front of the server answered 502 or 504. |

### State of waiting for the model server (`ModelServerWaitState`)

| Code | Meaning |
|---|---|
| `waiting` | Several calls in a row failed; the run holds every call and probes the server. |
| `resumed` | The server and the model answer again; held calls continue. |
| `given_up` | The server stayed away longer than `ModelServer.MaxWaitMinutes`; the run ends with exit code 4, open items stay open. |
