# Documentation

This directory contains the working project documentation for `Smart File Organizer`.

## Documents

- `product-overview.md` — product vision, target use cases, scope, and non-goals
- `status-and-roadmap.md` — current implementation status, shipped features, gaps, and next priorities
- `architecture.md` — solution structure, layer responsibilities, runtime flow, and key extension points
- `engineering-decisions.md` — important library choices, trade-offs, caveats, and lessons learned so far
- `code-guidelines.md` — repository-specific engineering and architectural guidelines
- `next-session-handover.md` — practical implementation handoff for the next coding/agent session

## Current summary

The repository already contains a functional vertical slice:

- recursive file scan into SQLite
- folder tree view with lazy expansion
- file table for the selected directory
- file details panel with image/text/PDF preview
- pause/cancel job controls
- asynchronous post-scan AI classification with pause/resume/stop controls
- Ollama model discovery, model selection, keep-alive configuration, and manual warm-up
- multimodal classification input preparation for images, PDFs, and text-like documents
- repository and service abstractions across Domain / Application / Infrastructure / Desktop

The largest missing product features are now the review/override workflow, stronger document understanding beyond preview-sized excerpts/first-page PDF renderings, and the final action pipeline for safe file moves/copies/archive operations.
