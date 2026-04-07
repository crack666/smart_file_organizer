# 🗂️ Smart File Organizer

> **AI-powered desktop app to tame your file chaos — locally, privately, offline.**

A cross-platform desktop application built with **.NET 10 + Avalonia** that recursively scans drives, USB sticks, backup folders and home directories — and uses a **local AI model via [Ollama](https://ollama.com)** to understand, classify and summarize your files without ever sending data to the cloud.

---

## ✨ What it does

| Feature | Status |
|---|---|
| 🔍 Recursive directory scan with smart skip rules | ✅ Done |
| 🧠 3-phase AI analysis pipeline (recon → per-file → directory summary) | ✅ Done |
| 🖼️ Image content analysis (visual AI via multimodal model) | ✅ Done |
| 📄 Document text extraction & classification | ✅ Done |
| 🎬 Video/audio classification by filename & path | ✅ Done |
| 🌲 Interactive folder tree with lazy loading | ✅ Done |
| 📊 File table with category color-coding, confidence & summary | ✅ Done |
| 🗒️ Directory AI summary banner (per-folder insight) | ✅ Done |
| 🔎 Reveal in Explorer (right-click folder) | ✅ Done |
| ✏️ Manual review & category override | ✅ Done |
| ⚙️ Configurable Ollama model, timeout, language | ✅ Done |
| 📦 Move/copy confirmed files to target folders | 🔜 Planned |
| 🧹 Trash candidate bulk actions | 🔜 Planned |

---

## 🚀 How it works

Smart File Organizer runs a **3-phase AI pipeline** after scanning your directory:

```
Phase 1 — Directory Reconnaissance
   ↳ For every folder: analyze file names, types, sizes
   ↳ Decide sampling strategy (analyze_all / random_sample / skip)
   ↳ Result stored in ai_directory_results (phase = 'pre_assessment')

Phase 2 — Per-File Deep Analysis
   ↳ Raster images (jpg/png/gif/webp) → visual AI (base64, up to 10 MB)
   ↳ SVG → XML read as text, classified semantically
   ↳ Other image formats (bmp/tiff/heic/raw/ico/…) → metadata only
   ↳ PDF → page 1 rendered to PNG → visual AI
   ↳ DOCX/XLSX/ODT/ODS → ZIP/XML text extraction → semantic classification
   ↳ Text & code files → direct UTF-8 read → semantic classification
   ↳ Videos/audio → filename + path heuristics (no bytes uploaded)
   ↳ Everything else → filename, path, size, date (no bytes uploaded)
   ↳ Returns: category, importance, confidence (0–100%), summary
   ↳ Result stored in ai_results

Phase 3 — Directory Summaries (bottom-up)
   ↳ Synthesizes Phase 2 results into one human-readable folder description
   ↳ Shown as a green banner when you select a folder
   ↳ Result stored in ai_directory_results (phase = 'summary')
```

All AI inference runs **100% locally** through Ollama. No API keys, no data leaves your machine.

---

## 🛠️ Tech Stack

| Layer | Technology |
|---|---|
| UI Framework | [Avalonia UI](https://avaloniaui.net) · .NET 10 |
| MVVM | CommunityToolkit.Mvvm |
| Database | SQLite via Dapper |
| AI Backend | [Ollama](https://ollama.com) (`/api/chat`) |
| Recommended Model | `gemma4:26b` (multimodal, fast with `think=false`) |
| Architecture | Clean Architecture — Domain / Application / Infrastructure / Scanning / Desktop |

---

## ⚡ Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Ollama](https://ollama.com) running locally on `http://localhost:11434`
- A multimodal model pulled, e.g.:
  ```bash
  ollama pull gemma4:26b
  ```

### Run

```bash
git clone https://github.com/youruser/smart-file-organizer
cd smart-file-organizer
dotnet run --project src/SmartFileOrganizer.Desktop
```

### Configuration

Settings are stored in `%APPDATA%\SmartFileOrganizer\settings.json` and override `appsettings.json`:

```json
{
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "Model": "gemma4:26b",
    "TimeoutSeconds": 180,
    "SummaryLanguage": "Deutsch"
  }
}
```

---

## 🗂️ Project Structure

```
src/
├── SmartFileOrganizer.Domain          # Entities, enums, interfaces
├── SmartFileOrganizer.Application     # Use-case services, AI pipeline orchestration
├── SmartFileOrganizer.Infrastructure  # SQLite persistence, Ollama HTTP client
├── SmartFileOrganizer.Scanning        # Scan engine, heuristics, FileType detection
└── SmartFileOrganizer.Desktop         # Avalonia UI, ViewModels, DI wiring
```

---

## 📂 File Type Support

Every file is analyzed — the approach just varies based on what content can actually be extracted.

### Raster Images — visual AI analysis

Sent to the vision model as a base64-encoded attachment.

| Extensions | Treatment |
|---|---|
| `.jpg` `.jpeg` `.png` `.gif` `.webp` | Full visual analysis (up to `MaxImageUploadBytes`, default 10 MB) |

If the file exceeds the size limit or cannot be read (e.g. corrupt checksum), classification falls back to filename + metadata only.

### Vector & Structured Images — text analysis

Vision models cannot decode these formats. The file content is read as text and sent as context instead.

| Extension | Treatment |
|---|---|
| `.svg` | XML source read as text (truncated at 4 KB), classified semantically |

### Other Image Formats — metadata only

These formats are not natively decodable by Ollama vision models (raw sensor data, legacy formats, icons).

| Extensions | Treatment |
|---|---|
| `.bmp` `.tiff` `.tif` `.heic` `.heif` `.raw` `.cr2` `.nef` `.arw` `.ico` | Filename, path, size, and modification date only |

### Documents — text extraction + semantic analysis

| Extensions | Treatment |
|---|---|
| `.pdf` | Page 1 rendered to PNG via PDFium → visual AI |
| `.docx` | `word/document.xml` extracted from ZIP, paragraphs sent as text |
| `.xlsx` | Shared strings + all worksheet cells extracted from ZIP, sent as text |
| `.odt` | `content.xml` extracted from ZIP, paragraph/heading nodes sent as text |
| `.ods` | `content.xml` extracted from ZIP, table-row/cell nodes sent as text (preview) |
| `.txt` `.md` `.csv` `.json` `.xml` `.html` `.htm` `.yaml` `.yml` `.ini` `.cfg` `.sql` `.log` | Read directly as UTF-8 text (truncated at 12 000 chars) |
| `.cs` `.js` `.ts` `.tsx` `.jsx` `.py` `.css` `.ps1` `.sh` `.bat` | Read as source code text |

### Videos — metadata only

Uploading video bytes would be impractical. Classification is based entirely on filename and directory path.

| Extensions | Treatment |
|---|---|
| `.mp4` `.mkv` `.avi` `.mov` `.wmv` `.flv` `.webm` `.ts` `.m4v` + others | Filename + directory path heuristics |

### Audio — metadata only

| Extensions | Treatment |
|---|---|
| `.mp3` `.flac` `.wav` `.aac` `.ogg` `.m4a` `.opus` + others | Filename + directory path heuristics |

### Archives & Installers — metadata only

Archive contents are not unpacked. Classification is based on the archive name, path, and size.

| Extensions | Treatment |
|---|---|
| `.zip` `.7z` `.rar` `.tar` `.gz` `.bz2` `.xz` `.dmg` `.iso` + others | Filename, path, size |

### Everything Else — metadata only

Any file type not covered above is still classified — the AI uses filename, extension, directory path, and size as signals. No bytes are ever uploaded for unknown formats.

---

## 🧠 AI Categories

Files are classified into one of:

`Photo` · `DocumentScan` · `Screenshot` · `Video` · `Audio` · `Document` · `Invoice` · `PersonalDocument` · `Letter` · `Archive` · `Code` · `SoftwareInstaller` · `SystemFile` · `TrashCandidate` · `ReviewNeeded` · `Unknown`

Each result also includes an **importance rating** (`Low / Medium / High / Critical`) and a **confidence score**.

---

## 🏗️ Roadmap

- [ ] File move/copy actions after review
- [ ] Bulk-select & apply operations
- [ ] Export report (CSV / JSON)
- [ ] Custom skip-pattern editor in UI
- [ ] Support for remote Ollama instances
- [ ] macOS / Linux packaging

---

## 📚 Internal Documentation

| Doc | Purpose |
|---|---|
| [status-and-roadmap.md](status-and-roadmap.md) | What's done, what's limited, what's next |
| [architecture.md](architecture.md) | Layer structure, runtime flows, DB schema |
| [next-session-handover.md](next-session-handover.md) | Practical handover for next dev/agent session |
| [engineering-decisions.md](engineering-decisions.md) | Library choices, trade-offs, lessons learned |
| [code-guidelines.md](code-guidelines.md) | Repo-specific conventions and rules |
| [product-overview.md](product-overview.md) | Vision, use cases, non-goals |

---

## 📄 License

MIT
- file details panel with image/text/PDF preview
- pause/cancel job controls
- asynchronous post-scan AI classification with pause/resume/stop controls
- Ollama model discovery, model selection, keep-alive configuration, and manual warm-up
- multimodal classification input preparation for images, PDFs, and text-like documents
- repository and service abstractions across Domain / Application / Infrastructure / Desktop

The largest missing product features are now the review/override workflow, stronger document understanding beyond preview-sized excerpts/first-page PDF renderings, and the final action pipeline for safe file moves/copies/archive operations.
