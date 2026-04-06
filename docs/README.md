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
   ↳ Images → visual AI (uploaded as base64, up to 10 MB)
   ↳ Documents → text extraction + semantic classification
   ↳ Videos → filename + path heuristics (no bytes uploaded)
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
