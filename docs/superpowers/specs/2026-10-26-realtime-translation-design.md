# Real-time Translation Design

## Intent

YueYuOrb will gain one shared real-time translation capability with three
user-facing entry points:

1. translate the current application's selected text;
2. OCR a screen region selected by the user and translate the recognized text;
3. continuously OCR and translate a fixed screen region for subtitles.

The three paths must share translation providers, language handling, caching,
request scheduling, cancellation, error reporting, and the same translation
overlay. A translation feature must not be implemented three separate times.

MyMemory is the default provider so translation works immediately without an
account or API key. Users can instead configure Azure Translator, DeepL,
LibreTranslate, or an OpenAI-compatible endpoint.

The product outcome is a usable, honest, privacy-conscious translation tool:

- ordinary selected text works across applications that support `Ctrl+C`;
- OCR screenshots remain local, and only recognized text is sent to a
  translation provider;
- subtitle mode limits capture and network work so it does not continuously
  overload the machine or a free API;
- API keys are encrypted with Windows DPAPI and never appear in
  `settings.json`, exported settings, diagnostics, or logs.

## Goals

- Add a single translation settings model and provider abstraction.
- Support the three approved translation routes from hotkeys, the wheel, and
  the settings UI where applicable.
- Provide a default no-configuration MyMemory provider.
- Support custom API endpoint, key, model, and Azure region as required by the
  selected provider.
- Preserve the clipboard after selected-text extraction and never persist the
  selected or recognized text.
- Use Windows OCR without adding a bundled OCR engine or language model.
- Detect subtitle-region changes and avoid repeated OCR/network calls for an
  unchanged frame or unchanged text.
- Display loading, success, and actionable failure states in a non-activating,
  always-on-top translation overlay that follows the app's light/dark theme.
- Keep existing settings files loadable and preserve the current
  "edit, then Apply" settings workflow.
- Keep Windows 10 22H2 x64 and Windows 11 x64 compatibility.

## Non-Goals

- Translate audio, microphone input, speech, or protected/DRM video.
- Read every application's internal selection model. The first release uses
  standard clipboard copy behavior.
- Guarantee OCR for handwriting, stylized text, very small text, or every
  language pack.
- Automatically install Windows language or OCR packs.
- Translate an entire monitor continuously or OCR every visible window in the
  background.
- Provide a permanent, unlimited, keyless official translation API.
- Store translation history or clipboard history.
- Support multiple simultaneous custom provider profiles in the first release.
- Translate non-text clipboard payloads such as images or files.
- Add a speech/translation sidecar service, driver, or privileged component.

## Anti-Goals

- Do not upload screenshots to a translation provider.
- Do not write API keys, selected text, OCR text, translations, or clipboard
  contents to settings, logs, crash reports, or telemetry.
- Do not automatically fall back from a failing custom provider to MyMemory or
  another provider. A different provider may have different privacy terms and
  unexpected fallback would be surprising.
- Do not retry quota, authentication, or invalid-request failures indefinitely.
- Do not inject Ctrl+C while the user is still holding the translation hotkey
  modifiers, because that can become `Ctrl+Alt+C` in the target application.
- Do not let subtitle capture feed its own overlay back into OCR.
- Do not replace the user's existing wheel choices when new actions are added.
- Do not block the WPF UI thread while copying, OCR, HTTP requests, or
  scrolling frames are running.

## Constraints

- The app targets `net10.0-windows`, WPF, WebView2, and x64 Windows 10/11.
- The Windows OCR implementation requires the Windows SDK projection and a
  minimum target of Windows 10 version 2004 (`10.0.19041`). This is also the
  baseline where `WDA_EXCLUDEFROMCAPTURE` is available.
- `AppSettings` is a positional record. New settings must use init-only
  properties with safe defaults instead of new positional constructor
  arguments.
- Settings are serialized as JSON with `SchemaVersion = 1`. The new fields are
  additive and must load from older files without a migration error.
- The existing settings model merges edited fields against the latest snapshot.
  Translation fields must preserve the same concurrent-edit behavior.
- Existing hotkey fallback behavior remains authoritative when a default
  translation chord is occupied by another application.
- The app already stores data in `%LOCALAPPDATA%\FloatDesk`.
- No external OCR executable, Python runtime, large language model, or bundled
  translation model is allowed as a default dependency.

## Approaches Considered

### 1. Separate translation implementation per entry point

Implement selected-text translation, OCR translation, and subtitle translation
as independent windows with separate provider code.

Trade-off: initially straightforward for each screen, but duplicates HTTP
handling, cache behavior, settings, errors, and security. Fixing a provider or
quota issue would require three changes. Rejected.

### 2. Unified translation center with three triggers (selected)

One provider contract, language layer, cache, scheduler, and overlay serve all
three entry points. Selected text, OCR, and subtitle mode are adapters that
produce text and translation context.

Trade-off: requires a small shared architecture up front, but keeps behavior
consistent, testable, and privacy-safe. This is the selected approach.

### 3. Fully offline OCR and translation engine

Bundle Tesseract and Argos Translate models for offline OCR and translation.

Trade-off: strongest offline privacy story, but adds large model downloads,
CPU-heavy inference, language-pair model management, and a significantly larger
portable package. It does not meet the requirement for a lightweight default
and is not selected for the first release.

### 4. Browser-extension or AI-browser-only translation

Translate only inside YueYuOrb's AI browser or a browser extension.

Trade-off: simpler page integration, but does not cover desktop applications,
PDF readers, video subtitles, or arbitrary screen content. Rejected.

## Product Behavior

### Settings

A new `实时翻译` tab is added to the existing settings navigation. It uses the
current ice-blue glass visual language and the existing bottom `应用` action.

The tab contains:

- `翻译服务`: MyMemory, Azure Translator, DeepL, LibreTranslate, or
  OpenAI-compatible.
- `目标语言`: defaults to `简体中文`.
- `原文语言`: defaults to `自动检测`; a user may choose a fixed language.
- `接口地址`: editable for custom-capable providers, read-only or hidden for
  fixed MyMemory behavior.
- `API 密钥`: masked; never populated from a persisted plaintext value.
- `模型`: required for OpenAI-compatible providers and optional where the
  provider accepts a model override.
- `Azure 区域`: shown only for Azure Translator.
- `OCR 语言`: defaults to the Windows user profile language and lists installed
  OCR recognizer languages.
- `字幕采样间隔`: default `450 ms`, allowed range `300-1500 ms`.
- `字幕浮层透明度`: default `0.92`, allowed range `0.60-1.00`.
- `字幕字号`: default `20 DIP`, allowed range `14-36 DIP`.
- `测试连接`: sends a short fixed test string using the staged settings and key,
  without saving them.
- `免费服务说明`: explains that MyMemory needs no key, has daily and
  per-request limits, and may fail when quota is exhausted.
- `隐私说明`: screenshots remain local; only selected or OCR-recognized text is
  sent to the selected translation provider.

The normal settings workflow remains unchanged: changing controls does not
apply immediately; `应用` validates and saves, then the runtime reconfigures
translation. A saved API key is indicated by a non-secret "已保存" status and
can be cleared explicitly.

### Shortcuts and wheel entries

Add three hotkey actions:

- `TranslateSelection`: default `Ctrl+Alt+T`.
- `TranslateRegion`: default `Ctrl+Alt+O`.
- `ToggleSubtitleTranslation`: default `Ctrl+Alt+L`.

The shortcuts are editable in the existing hotkey section. If a chord is
unavailable, the existing hotkey fallback and warning behavior applies.

Add three wheel actions:

- `翻译选中`
- `框选翻译`
- `字幕翻译`

New settings include them in the default wheel. Existing settings retain their
saved wheel choices; the actions can be enabled in `设置 > 轮盘`. The wheel
already supports scrolling and does not need a new submenu.

### Selected-text route

1. The user presses `Ctrl+Alt+T` while text is selected in another application.
2. The runtime records the foreground window and verifies it again immediately
   before input injection.
3. It waits briefly for the translation hotkey's physical modifiers to be
   released. It does not inject Ctrl+C while Alt, Ctrl, Shift, or Win remain
   held.
4. It snapshots the current clipboard and records the clipboard sequence
   number.
5. It sends Ctrl+C with `SendInput`, waits up to 800 ms for a new clipboard
   sequence number, and reads Unicode text.
6. It restores the clipboard snapshot in a `finally` block.
7. It shows a non-activating translation overlay near the current pointer, with
   `正在读取` replaced by the translated result.

If no text is copied, the target rejects input, the foreground window changed,
or only a non-text clipboard format is produced, the overlay reports
`未检测到可翻译的选中文字` without sending anything.

The clipboard snapshot/restore is best-effort for formats Windows permits the
application to copy. It must not become a persistent clipboard manager. The
selected text and snapshot live only in memory and are disposed or cleared
after the operation.

### OCR region route

1. The user invokes `框选翻译` from the hotkey or wheel.
2. The app checks whether a Windows OCR recognizer is available for the selected
   or profile language.
3. It uses the same frozen-desktop region-selection pattern as screenshot
   capture, but does not open the annotation editor or save a file.
4. It crops the selected physical-pixel region and passes the bitmap to Windows
   OCR.
5. It sends only the normalized recognized text to the translation provider.
6. It shows the original recognized text and translation in the shared overlay
   near the selected region.

An empty OCR result reports `未识别到文字`. A missing OCR language pack reports
an actionable message and offers to open Windows language settings. Screenshot
frames and cropped bitmaps are never written to disk or included in logs.

### Fixed-region subtitle route

The first invocation asks the user to select a subtitle region. Later
invocations reuse the saved region if its monitor is still available. The
region is stored as monitor-relative coordinates so normal window movement and
DPI changes do not corrupt it.

While active:

- capture only the selected local region at the configured interval;
- compare a downscaled frame signature and skip OCR when the image is stable;
- debounce a changed frame briefly, then OCR the latest stable frame;
- skip translation when normalized OCR text is unchanged or nearly identical to
  the previous text;
- allow only one subtitle OCR/translation pipeline at a time and replace queued
  stale work with the newest frame;
- update the same persistent overlay in place.

The overlay provides `暂停/继续`, `重新选择区域`, and `停止` controls. The same
hotkey toggles the session: start when inactive, stop when active. Pausing or
stopping cancels pending OCR/translation work and stops the timer. The overlay
does not auto-hide in subtitle mode.

Subtitle mode pauses when:

- YueYuOrb's emergency pause is active;
- the Windows session is locked;
- a screenshot capture is in progress;
- the saved monitor disappeared after a display topology change;
- the user pauses or stops the session.

Network or provider failures do not destroy the session automatically. The
overlay shows the failure and retries only after a bounded backoff. Authentication
or unsupported-language failures stop automatic retries until the user changes
settings.

### Translation overlay

All three routes use one `TranslationOverlayWindow` with these states:

- preparing/extracting text;
- translating;
- result ready;
- error with a short actionable message.

The overlay displays:

- original text, when available;
- translated text;
- source language or detected language;
- provider name;
- loading or failure status;
- close and copy-translation actions.

It is topmost within the existing overlay policy, `ShowActivated=false`, absent
from the taskbar, and must not take keyboard focus. It uses the existing theme
brushes and follows light/dark changes without restarting. Transitions are short
fades/translations and respect `SystemParameters.ClientAreaAnimation` and high
contrast.

One-shot overlays are placed near the pointer or selected region, clamped to the
monitor work area. Subtitle overlays use a saved position and default to the
bottom center, above the taskbar, outside the selected capture region.

## Translation Providers

### Provider contract

Every provider implements one interface and returns typed success/failure
results:

```csharp
public interface ITranslationProvider
{
    string Id { get; }
    TranslationProviderCapabilities Capabilities { get; }

    Task<TranslationProviderResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken);
}
```

The request contains text, canonical source/target language tags, purpose
(`Interactive` or `Subtitle`), and no UI types. The result contains either a
translation plus optional detected language, or a typed failure such as
network, timeout, quota, authentication, invalid request, unsupported language,
provider unavailable, or empty result.

Provider capabilities describe native auto-detection, maximum text size, and
request concurrency. The translation center owns scheduling, caching,
cancellation, and user-facing error mapping.

### Built-in provider behavior

| Provider | Key | Default endpoint | Notes |
| --- | --- | --- | --- |
| MyMemory | Not required | MyMemory public API | Anonymous daily/per-request limits; default provider |
| Azure Translator | Required | `https://api.cognitive.microsofttranslator.com` | Endpoint and region configurable |
| DeepL | Required | `https://api-free.deepl.com` or user endpoint | Supports Free/Pro-compatible endpoints |
| LibreTranslate | Optional | Empty; user supplied | Self-hosted/custom endpoint; key sent in request body |
| OpenAI-compatible | Required | Empty; user supplied | Requires an HTTPS chat-completions endpoint and model |

Provider-specific adapters map canonical language tags to the provider's
required codes. Unsupported pairs are rejected before an HTTP request when the
adapter can determine that they cannot work.

### MyMemory free provider

MyMemory is the default because it requires no credential and works after
installation. The provider:

- calls the official HTTPS endpoint with a `langpair`;
- decodes HTML entities in the returned text;
- rejects warning bodies, quota failures, and malformed responses;
- splits long input on paragraph/sentence boundaries into UTF-8 chunks of at
  most 450 bytes, below the documented 500-byte request limit;
- sends at most ten chunks for one interactive request, then asks the user to
  select less text;
- uses one in-flight request and a client-side minimum interval between chunks.

MyMemory does not provide a reliable native auto-detect contract for this
feature. When source language is `自动检测`, the app resolves a best-effort
canonical source language locally before calling MyMemory. The overlay shows the
resolved source; the user can select a fixed source if detection is wrong.

Anonymous MyMemory usage is limited. The official documentation must be checked
again during implementation, and the UI must not promise unlimited free
translation. A quota response is surfaced as a quota failure with a suggestion
to configure another provider. The provider uses GET and therefore sends text
in the request URL; the privacy note must state this. YueYuOrb itself does not
log the URL or text.

### Custom provider endpoints

Custom endpoints must:

- use HTTPS;
- be absolute URIs with a non-empty host;
- contain no username/password, fragment, or API key in the URL;
- reject redirects to non-HTTPS targets;
- use the system certificate validation and proxy configuration;
- never bypass TLS errors.

The API key is sent in the provider-defined authentication header/body and is
never placed in a log message. HTTP tracing is disabled for these clients.

The OpenAI-compatible adapter sends a non-streaming chat-completions request
with a fixed system instruction to return only the translation. The
configuration receives the complete chat-completions URL, not an OpenAPI base
URL, so there is no ambiguous path rewriting. The model is required. No
user-supplied system prompt is supported in the first release.

## Settings Model and Compatibility

### Domain types

Add canonical language constants and a provider enum in `FloatDesk.Core`.
Suggested settings shape:

```csharp
public enum TranslationProviderKind
{
    MyMemory,
    AzureTranslator,
    DeepL,
    LibreTranslate,
    OpenAiCompatible
}

public sealed record TranslationSettings(
    TranslationProviderKind Provider,
    string SourceLanguage,
    string TargetLanguage,
    string Endpoint,
    string Model,
    string AzureRegion)
{
    public int SubtitleRefreshIntervalMs { get; init; } = 450;
    public double SubtitleOverlayOpacity { get; init; } = 0.92;
    public double SubtitleFontSizeDip { get; init; } = 20;
    public string? OcrLanguage { get; init; }
    public TranslationCaptureRegion? SubtitleRegion { get; init; }
    public TranslationOverlayPlacement? SubtitleOverlayPlacement { get; init; }
}
```

`AppSettings` gains an init-only property:

```csharp
public TranslationSettings Translation { get; init; } =
    TranslationSettings.CreateDefault();
```

The default is:

- provider MyMemory;
- source `auto`;
- target `zh-Hans` (`简体中文`);
- subtitle sampling `450 ms`;
- overlay opacity `0.92`;
- subtitle font `20 DIP`;
- OCR language from the Windows user profile;
- no saved subtitle region or overlay placement.

`TranslationCaptureRegion` stores monitor identity plus relative X/Y/width/height
for the capture rectangle. `TranslationOverlayPlacement` stores monitor identity
plus relative X/Y for the caption overlay. Both are added inside
`TranslationSettings` rather than `AppSettings.Windows`, because the current orb
position update replaces the `Windows` collection and would erase unrelated
placements.

### Validation and migration

- Validate enum values, non-empty language tags, subtitle ranges, opacity and
  font ranges, monitor-relative coordinates, endpoint syntax, and conditional
  provider requirements.
- Azure requires an endpoint and key but region may be optional for a
  single-region resource.
- DeepL requires a key.
- LibreTranslate requires an endpoint; key is optional.
- OpenAI-compatible requires an HTTPS endpoint and model.
- MyMemory ignores custom endpoint/model fields and needs no key.
- Do not bump `SchemaVersion` for additive optional fields.
- On load, backfill the three new default hotkey entries when absent from an
  older hotkey dictionary. Do not modify an existing saved hotkey.
- Old JSON without `translation` loads the default translation settings.
- Old `WheelActions` are preserved. Only new/default settings include the three
  translation wheel entries.

### Settings merge behavior

Add translation fields to `SettingsViewModel.From` and `BuildCandidate` using
the existing field-by-field baseline comparison. Any field the user did not
edit preserves the latest concurrently saved value. A changed field wins.

Do not put the API key in `SettingsViewModel` persistence data. The ViewModel
may temporarily hold a staged key for the active edit session and `测试连接`,
but it is cleared after Apply/Cancel and never assigned into `AppSettings`.

### Encrypted API key storage

Add an `ITranslationCredentialStore` contract. The Windows implementation:

- lives in `FloatDesk.Windows`;
- stores one versioned encrypted blob under
  `%LOCALAPPDATA%\FloatDesk\translation-secrets.dat`;
- protects the complete blob with
  `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`;
- writes atomically through a temporary file and replace;
- stores credentials keyed by provider kind plus a stable endpoint identifier,
  so changing the host does not silently send the old key to the new host;
- treats a missing/corrupt/foreign-user blob as "key not available" rather than
  crashing;
- has no plaintext fallback.

Settings export never includes translation credentials. Importing settings on
another machine may therefore require the user to enter the key again.

## Translation Center

The translation center is provider-agnostic and has no WPF dependency.

### Request flow

1. Normalize and validate source text.
2. Resolve source language (native auto-detect when supported, otherwise the
   local detector) and reject a target/source collision with a clear
   "already in target language" result.
3. Build a cache key from normalized text, source/target tags, provider
   identity, endpoint identity, and model.
4. Return a valid cached result, or join an identical in-flight request.
5. Apply the provider rate/concurrency gate.
6. Chunk when required by provider limits.
7. Call the provider with a timeout and caller cancellation token.
8. Normalize and join translated chunks.
9. Store a bounded in-memory cache entry only when successful.
10. Return a typed result to the active route/overlay.

Cache and in-flight entries are memory-only, bounded, and cleared when provider
configuration changes or the application exits. No cache is written to disk.

### Language detection

The local detector is deterministic and best-effort:

- Unicode script detection handles Han, Kana, Hangul, Cyrillic, Arabic, Hebrew,
  Thai, Devanagari, and common Latin-script languages;
- Han with Kana is Japanese; Hangul is Korean; otherwise Han defaults to
  Simplified Chinese unless a Traditional-character signal is strong;
- Latin text uses small stopword/character-frequency scoring;
- low-confidence Latin text uses English as the fallback source and exposes the
  detected language in the overlay;
- an explicit source language always overrides detection.

Providers that reliably support native auto-detection receive `auto` directly;
providers that do not receive the resolved canonical tag.

### Rate limiting, deduplication, and priority

- Provider capabilities define maximum concurrency and minimum interval.
- MyMemory defaults to one request at a time and a minimum interval between
  chunks.
- Interactive translation has priority over subtitle work. If a subtitle
  request occupies the only provider slot, it may be cancelled and restarted
  after the interactive request completes.
- Subtitle work always keeps only the newest pending frame. It never builds an
  unbounded queue.
- Identical normalized requests share one in-flight task.
- Transient network/5xx failures receive at most two bounded retries with
  exponential backoff. Timeout, 401/403, 429/quota, invalid request, and
  unsupported-language failures are not retried automatically.
- A 429/quota response pauses subtitle retries for a bounded cool-down and shows
  an actionable status.

### Text limits and normalization

- Interactive input is capped so the UI and quota remain predictable; the
  adapter may reject or chunk before that cap.
- Subtitle OCR text is capped to a subtitle-sized maximum. Oversized OCR output
  is truncated at a line boundary with a visible error/status rather than sent
  in full silently.
- Normalize line endings and excessive whitespace while preserving paragraph
  boundaries.
- Join provider chunks in order and preserve blank-line separation. Use a
  target-aware separator between sentence chunks.

## Windows Integration

### Selected text

Implement selected-text extraction behind an injectable service so it can be
unit tested with a fake clipboard and foreground monitor:

1. verify the recorded foreground window is still current and not the secure
   desktop;
2. wait for physical hotkey modifiers to be released, bounded to 1 second;
3. snapshot clipboard formats and the clipboard sequence number;
4. use `SendInput` for Ctrl+C;
5. wait for a sequence change and read Unicode text with a short timeout;
6. restore the snapshot in `finally`;
7. return an empty/denied result without retaining clipboard data.

The first release intentionally uses the standard clipboard copy path rather
than UI Automation. This provides broad compatibility with browsers, editors,
PDF readers, and Office applications. It must not attempt to inspect password
fields or retain any clipboard payload.

### Windows OCR

Add a Windows OCR adapter in `FloatDesk.Windows` using
`Windows.Media.Ocr`/`Windows.Graphics.Imaging` through the Windows SDK
projection. It:

- enumerates available recognizer languages;
- creates an engine from the configured installed language or the user profile;
- converts cropped BGRA pixels to a `SoftwareBitmap`;
- reads `OcrResult.Lines` in reading order and joins them with line separators;
- disposes bitmaps and WinRT objects promptly;
- returns a typed "language pack missing" or "no text" result.

The OCR adapter does not write files and does not upload images. The project
target/platform baseline is updated to Windows 10 2004-compatible SDK metadata
as required by the projection; this preserves Windows 10 22H2 x64 and Windows
11 x64 support without adding a third-party native OCR runtime.

### Region selection

Extract the frozen-desktop selection currently embedded in
`CaptureCoordinator` into a reusable `RegionSelectionSession` (or an equivalent
small coordination service) used by:

- screenshot capture;
- OCR translation;
- first-time subtitle region selection.

The shared selector:

- hides YueYuOrb's own visible windows as the existing capture flow does;
- supports all monitors and the virtual desktop;
- returns physical-pixel bounds relative to the frozen frame;
- is cancellable and restores visibility on every exit path;
- does not open the screenshot editor or save a file.

All capture paths share a capture gate so a screenshot and OCR selection cannot
overwrite each other's state.

### Fixed-region capture and change detection

- Capture only the crop rectangle, not the whole desktop.
- Use a downscaled grayscale signature/average hash to compare frames.
- Skip OCR when the signature difference is below the configured threshold.
- After a significant change, wait one short stability interval (default
  `120 ms`) and capture again before OCR.
- Normalize OCR text and compare it with the prior text using exact and
  high-similarity checks; skip redundant translations.
- Use the configured sample interval, default `450 ms`; the minimum accepted
  interval is `300 ms`.
- Do not use a persistent image/stream; capture, OCR, and hash buffers are
  short-lived.

### Overlay exclusion from subtitle capture

Translation overlays and the subtitle text are marked with
`WDA_EXCLUDEFROMCAPTURE` when Windows accepts it. The default subtitle overlay
placement is outside the selected region. If display-affinity exclusion fails
and the overlay intersects the capture rectangle:

- move the overlay to the nearest non-overlapping work-area position and show a
  one-time notice; or
- pause subtitle mode with an actionable status if no safe position exists.

This prevents OCR feedback loops. The user can override the position, but a drop
inside the capture region is rejected or corrected.

### Screen capture coordination

Before any screenshot or OCR selection:

- pause subtitle sampling;
- hide the translation overlay through the existing capture visibility path;
- restore both after the capture session ends.

Subtitle mode also pauses during emergency input pause and Windows session lock.
Display topology changes resolve the saved monitor; if it is missing, subtitle
mode pauses and requests a new region.

## Overlay and Focus Behavior

Create `TranslationOverlayWindow` in `FloatDesk.App`:

- `WindowStyle=None`, transparent/ice-blue glass surface, no taskbar entry;
- `ShowActivated=false`;
- `WS_EX_NOACTIVATE` and tool-window behavior when possible;
- topmost according to the existing overlay policy;
- non-focusing enough that it does not disturb the source application after
  selected-text extraction;
- pointer placement clamped to the nearest monitor work area;
- draggable only by its own title/surface handle, with the position saved for
  subtitle mode;
- action buttons remain clickable without activating the underlying window
  unless Windows explicitly requires activation for that control.

The overlay must use the existing theme resource keys and dynamic resources. It
does not introduce a second visual style. Loading/result transitions are short
and disabled under reduced-motion/high-contrast settings.

## Runtime Lifecycle

- `AppRuntime` owns a long-lived translation controller.
- The controller receives settings snapshots and the encrypted credential
  store.
- `AppSettings.Translation` changes rebuild the provider only when provider,
  language, endpoint, model, region, OCR language, or display behavior changed.
  Saving a new subtitle region/overlay placement alone does not cancel an
  active subtitle session.
- Hotkey events are dispatched to the WPF UI thread for UI work; network calls
  remain asynchronous and cancellable.
- The wheel routes call the same controller methods as hotkeys.
- `ApplySettings` updates overlay theme and provider configuration without
  requiring an app restart.
- Settings `Cancel` discards staged translation controls and any staged key.
- Exit cancels OCR/translation timers, cancels in-flight requests, disposes
  capture/OCR resources, clears caches, and disposes the overlay.

## Security and Privacy

- Screenshots never leave the process. Only selected/OCR-recognized text is
  sent to the selected provider.
- API keys are protected with DPAPI CurrentUser and never serialized into
  `settings.json` or export files.
- Do not log text, translations, clipboard contents, image data, request URLs,
  authorization headers, or request bodies.
- Diagnostic events may include provider kind, mode, character-count bucket,
  latency, result category, retry count, and error kind. No content.
- MyMemory's free endpoint receives text in a GET URL. The UI must disclose
  that text is sent to a third-party service and that query strings may be
  visible to network intermediaries. Users can select a custom provider for
  more control.
- Custom endpoints must use HTTPS and default OS certificate validation.
- The app must not store translation history or clipboard history.
- Text/translation overlays and caches are cleared when their session ends or
  the application exits.

## Failure and Fallback Behavior

| Failure | Behavior |
| --- | --- |
| No selected text / copy rejected | Overlay shows `未检测到可翻译的选中文字`; nothing is sent |
| Clipboard access denied | Overlay shows a clipboard permission/status message; nothing is sent |
| OCR language pack missing | Actionable message and a link/button to Windows language settings |
| OCR returned empty text | Overlay shows `未识别到文字`; nothing is sent |
| Provider missing key | Settings/overlay explains which provider needs a key |
| Endpoint invalid | Settings validation prevents Apply |
| Network offline | Typed network error; retry only when explicitly allowed by the bounded policy |
| 401/403 | Authentication failure; no automatic retry |
| 429/quota | Quota message; subtitle pauses/backs off; suggest another provider |
| Provider timeout | Cancellable error; interactive overlay remains usable |
| Unsupported language pair | Clear message; no screenshot/text deletion |
| Subtitle region saved on a missing monitor | Pause and request reselection |
| Overlay would be captured | Reposition or pause subtitle mode |
| App exits/session locks | Cancel work, stop timers, clear in-memory content |

There is no silent provider fallback. A failed provider does not unexpectedly
send text to a different service.

## Test Strategy

### Core tests

- language detection and provider language mapping;
- source/target collision handling;
- MyMemory UTF-8 chunking and ordered joining;
- cache hit/expiry/provider-change invalidation;
- identical-request deduplication;
- interactive-over-subtitle priority;
- subtitle state machine start/pause/resume/stop;
- frame-signature change threshold and OCR text de-duplication;
- typed failure mapping and retry policy;
- cancellation prevents late result application;
- settings merge preserves untouched concurrent values and applies edited ones.

### Infrastructure tests

- MyMemory request shape, 500-byte chunk limit, entity decoding, quota/warning
  handling;
- Azure, DeepL, LibreTranslate, and OpenAI-compatible request headers/body/JSON
  parsing with a fake HTTP handler;
- HTTPS endpoint validation, redirect rejection, timeout, 401/403/429/5xx;
- no key/text/body appears in diagnostic data;
- credential store encrypts/decrypts with a fake protected-data adapter and
  fails safely for corrupt/foreign blobs.

### Windows tests

- clipboard snapshot/restore order and sequence-number wait behavior using a
  fake clipboard adapter;
- modifier-release guard prevents premature Ctrl+C;
- OCR bitmap conversion and line ordering with a deterministic fake OCR result;
- missing OCR language returns a typed result;
- monitor-relative region conversion across DPI/monitor changes;
- display-affinity fallback selection.

### UI tests

- translation tab fields, labels, provider-dependent visibility, and Apply/Cancel;
- new hotkey fields format/validation and merge behavior;
- wheel toggle list includes the three translation actions;
- overlay state text, theme resources, non-activating window style, and
  work-area clamping;
- wheel click routes and hotkey dispatch reach the shared controller;
- old settings without translation data produce safe defaults.

### Manual Windows 10/11 x64 checks

- select text in Edge, Notepad, Word, and a PDF reader and translate it;
- preserve text, image, and file-list clipboard contents after the selected-text
  route;
- use a non-English input mode and verify hotkey modifier release before Ctrl+C;
- OCR a browser region, a subtitle region, and a mixed-language region;
- test with an installed and an absent OCR language pack;
- run a video subtitle region for at least 30 minutes and verify no capture
  feedback/flicker, bounded CPU, and no request storms;
- verify subtitle overlay can be moved, paused, resumed, reselected, and stopped;
- test 125/150/200% DPI and a multi-monitor removal;
- test network offline, provider timeout, 401, 429, and quota responses;
- inspect `settings.json`, logs, and exported settings for secrets/content;
- test app exit, session lock, emergency pause, and screenshot overlapping
  subtitle mode;
- confirm the existing orb, wheel, AI browser, notes, input lock, and screenshot
  tests still pass.

## Acceptance Criteria

- All three translation routes are available from their approved hotkeys and
  wheel actions.
- MyMemory works without configuration after a fresh install and clearly
  states its free-service limits.
- Users can configure each approved provider with an endpoint/key/model/region
  where applicable.
- Custom endpoints require HTTPS and keys are encrypted with DPAPI CurrentUser;
  exported/settings JSON never contains a key.
- Selected-text translation preserves clipboard contents in the normal case
  and does not inject Ctrl+C while hotkey modifiers are held.
- OCR screenshots and subtitle frames are never uploaded or written to disk;
  only recognized text is sent.
- OCR reports missing language packs and empty results honestly.
- Subtitle mode samples only the fixed region, suppresses unchanged work, keeps
  one latest pending item, and can be paused/stopped/reconfigured.
- The overlay follows the existing theme, does not take focus, and never blocks
  the source application's normal keyboard behavior.
- Existing settings files load, existing wheel choices survive, and all
  existing tests continue to pass.
- Release build has zero warnings and zero errors; the full automated test
  suite passes.

## Builder Check

The design was checked from the perspective of an implementer joining the
existing codebase:

- Keep `TranslationSettings` as an init-only property on the positional
  `AppSettings` record; do not add a constructor parameter.
- Preserve existing wheel choices. New translation wheel entries are available
  in settings and are defaults only for newly created settings.
- Backfill missing translation hotkeys during load without overwriting a
  user-saved chord.
- Store subtitle region and overlay placement in `TranslationSettings`, not in
  `AppSettings.Windows`, because orb position persistence already replaces that
  collection.
- Rebuild/cancel translation work only for behavior-changing settings, not for
  a persisted region or overlay position.
- Use the local language detector for MyMemory because its request contract
  requires an explicit language pair; expose the resolved language in the
  overlay.
- Keep MyMemory chunks below 500 UTF-8 bytes and cap chunk count to avoid
  accidental quota exhaustion.
- Delay Ctrl+C until the physical hotkey modifiers are released; otherwise a
  normal copy command becomes a different shortcut.
- Use a short-lived clipboard snapshot rather than a history store.
- Use Windows OCR with the Windows SDK projection; do not bundle Tesseract,
  Argos, or another offline model in the default package.
- Mark translation overlays with `WDA_EXCLUDEFROMCAPTURE`, and make the
  no-overlap fallback explicit to prevent subtitle OCR feedback.
- Pause subtitle sampling during screenshot/OCR selection, emergency pause, and
  session lock.
- Do not automatically fall back between providers; fail with an honest,
  actionable status.
- Keep logs content-free and clear all in-memory text/caches on session stop or
  application exit.
- Verify the latest MyMemory limits during implementation and keep the UI
  limitation text conservative.
