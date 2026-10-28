# Translation usability fixes

User-authorized scope: fix region OCR returning no text, wheel selected-text activation, and undraggable results; simplify results to selectable/copyable text without visible product branding; add independent region text extraction.

1. Native selection activation: preserve a valid target identity while inactive, then restore and verify focus without bypassing process/window identity checks. Worker owns SelectedTextReader.cs and its tests.
2. Native OCR: reproduce with generated text fixtures and installed OCR languages, improve only evidence-backed empty-result handling. Worker owns WindowsScreenOcrService.cs and its tests.
3. Interactive overlay: compact grip/copy/close toolbar, readonly selectable result text, no visible brand/original/provider decoration; mouse dragging applies to interactive cards, subtitles remain transparent and click-through. Keep user-moved position across result updates in the same operation. User clicking result may activate for Ctrl+C; initial reading/status must not steal target focus.
4. Region extraction: reuse the existing guarded region capture and local OCR; return extracted text without initializing/calling a translation provider. Add a distinct wheel action, settings toggle, configurable hotkey and tray entry. Preserve schema 1 and old hotkey values.
5. Pin the original source snapshot when opening the wheel; an explicit captured target must not be replaced by a later foreground snapshot. Ordinary hotkeys continue to use the current external foreground.
6. RED/GREEN regression tests, full Release suite and zero-warning build, independent review, local self-contained test build. Preserve crystal mode; do not stop the user's process or publish to GitHub.

Privacy: no persistent selection cache, source/recognized/result content logging, captured images or secrets. Native OCR fixtures contain only generated test text.
