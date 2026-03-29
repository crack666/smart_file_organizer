# Next Session Handover

## Current product state

`Smart File Organizer` now has a working vertical slice for:

- scanning large directory trees into SQLite
- browsing directories/files in the desktop UI
- viewing file details and previews
- automatically starting post-scan AI classification
- pausing/resuming/stopping AI classification independently from the scan
- configuring Ollama base URL, model, and `keep_alive` in the UI
- warming the chosen Ollama model before a longer classification run

## What changed most recently

### Ollama integration

- classification is now really wired into the product flow after scan completion
- local models are loaded from Ollama with `/api/tags`
- the selected model is persisted to `%AppData%\\SmartFileOrganizer\\settings.json`
- the default high-end model remains `qwen3-vl:30b`
- `keep_alive` is supported and exposed in the UI
- a warm-up action can preload the model into VRAM using an empty `/api/chat` request

### Real multimodal classification

- AI requests now use `/api/chat`
- image files are sent as base64 images
- PDFs are represented by the rendered first page image
- text-like files contribute extracted preview text
- metadata remains part of the prompt for path/extension/context clues
- structured JSON output is requested via an explicit schema

## Important files to know

### Ollama and AI pipeline

- `src/SmartFileOrganizer.Infrastructure/Ollama/OllamaService.cs`
  - local model listing
  - warm-up requests
  - multimodal `/api/chat` classification
  - `keep_alive` handling

- `src/SmartFileOrganizer.Application/Services/AiClassificationCoordinator.cs`
  - separate post-scan AI execution flow
  - pause/resume/cancel semantics

- `src/SmartFileOrganizer.Application/Services/ClassificationService.cs`
  - batch iteration over AI-eligible files
  - persistence of results/status

- `src/SmartFileOrganizer.Desktop/Services/AiClassificationInputPreparer.cs`
  - converts files into multimodal evidence for Ollama

- `src/SmartFileOrganizer.Infrastructure/Ollama/OllamaOptions.cs`
  - model/base URL/timeout/keep-alive settings

### UI and settings

- `src/SmartFileOrganizer.Desktop/ViewModels/MainViewModel.cs`
  - toolbar actions for model loading/saving/warm-up
  - scan + AI orchestration and refresh behavior

- `src/SmartFileOrganizer.Desktop/Views/MainWindow.axaml`
  - toolbar layout including AI status and Ollama controls

- `src/SmartFileOrganizer.Desktop/Services/OllamaSettingsService.cs`
  - persists mutable Ollama settings to app data

- `src/SmartFileOrganizer.Desktop/appsettings.json`
  - built-in defaults distributed with the app

## Verified behavior from the last session

- `dotnet build SmartFileOrganizer.slnx` succeeds
- the model list can be read from the running Ollama server
- a live `/api/generate` request with a valid local model succeeded during debugging
- the earlier 404s were caused by a non-existent model name, not by network failure
- the current default model is intentionally a large vision-capable local model because the target machine has sufficient VRAM

## Current known limitations

### AI understanding limitations

- PDFs currently contribute only the first rendered page, not full-document reasoning
- text extraction is preview-sized, not a full content ingestion pipeline
- there is no OCR subsystem yet beyond what the vision model can infer from image/PDF render inputs
- Office formats and more complex binaries are not yet deeply parsed

### Product workflow limitations

- review/override UI is still missing
- action planning/execution (move/copy/archive to NAS or target folders) is still missing
- per-file AI errors are persisted but not yet surfaced in a rich review experience
- the AI pipeline is intentionally conservative and mostly sequential; it has not been aggressively tuned for throughput yet

## Suggested next priorities

1. **Improve document understanding quality**
   - multi-page PDF support
   - richer text extraction / OCR
   - prompt/schema refinement around “valuable user data vs trash”

2. **Build the review workflow**
   - show AI summary/confidence/errors more explicitly
   - allow category/target overrides
   - let the user confirm or reject suggestions

3. **Design the action pipeline**
   - generate safe file operation plans
   - preview changes before execution
   - add logging and rollback-minded behavior

4. **Profile AI throughput**
   - decide whether sequential processing is still the right default
   - consider bounded concurrency only after measuring real gains and UX trade-offs

## Operational notes for the next agent

- do not move AI inference into the scan loop unless there is a very strong reason and hard evidence that it will not damage scan responsiveness
- preserve the OSS-friendly dependency stance
- preserve the current default model unless the user explicitly wants a different default
- if testing Ollama behavior, check `/api/tags` first to verify the exact installed model names
- remember that a large model may need a long initial load into VRAM; `keep_alive` and manual warm-up are expected parts of the workflow