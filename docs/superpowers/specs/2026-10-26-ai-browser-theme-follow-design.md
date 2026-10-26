# AI Browser Theme Follow Design

## Intent

FloatDesk's AI browser currently looks independent from the rest of the
application. When the app is dark, an AI site can still render a bright white
page, as seen on DeepSeek. The user selected the mixed approach: make the AI
browser follow FloatDesk through WebView2's native color-scheme support, and
add narrowly scoped site adapters only where a site ignores the standard
mechanism.

The product outcome is:

- AI sites that respect `prefers-color-scheme` follow FloatDesk automatically.
- Supported sites such as DeepSeek are asked to use their own light/dark state
  when they do not reliably follow the native signal.
- The user can override the behavior with Light or Dark in the AI browser
  settings.
- No global CSS inversion, arbitrary script rewriting, or artificial recoloring
  of third-party pages is allowed.

## Goals

- Add an AI browser appearance setting with three choices:
  - `跟随 FloatDesk` (default)
  - `浅色`
  - `深色`
- Persist the setting with the existing settings file.
- Resolve `跟随 FloatDesk` against FloatDesk's effective theme, including the
  existing `System`, `Light`, and `Dark` application themes.
- Set `CoreWebView2Profile.PreferredColorScheme` before the first navigation so
  supporting pages receive the correct `prefers-color-scheme` value and the
  WebView2 UI uses the same light/dark preference.
- Reapply the effective scheme when:
  - the AI browser initializes or recovers from a failed WebView process;
  - any AI browser setting is applied;
  - FloatDesk's theme changes;
  - the Windows app theme changes while FloatDesk follows the system;
  - a page navigates;
  - an AI login popup is created or navigates.
- Add an exact-host adapter for `chat.deepseek.com` that uses DeepSeek's own
  observed theme state, `html[data-ds-dark-theme]`, rather than replacing its
  CSS.
- Keep the page, its scripts, login state, navigation policy, permission policy,
  download policy, and zoom behavior unchanged.

## Non-Goals

- Force every arbitrary website to support dark mode.
- Guess or synthesize adapters for unverified sites.
- Persist or mutate a site's own saved theme preference without an officially
  supported site mechanism.
- Modify third-party iframes or pages embedded in a supported page.
- Change the external/default browser.
- Replace WebView2's context menus, dialogs, or native controls with custom UI.
- Override Windows high-contrast accessibility colors.
- Guarantee a site's appearance if the site itself later overrides its theme
  after FloatDesk has applied it.

## Anti-Goals

- Do not invert arbitrary page colors with a global `filter`, `mix-blend-mode`,
  or blanket CSS override.
- Do not inject CSS selectors derived from a site's private implementation for
  the purpose of restyling unrelated elements.
- Do not read, copy, expose, or persist credentials, cookies, tokens, or page
  content while applying a theme adapter.
- Do not break navigation or display a fatal error when a runtime lacks the
  preferred-color-scheme API or when an adapter selector changes.
- Do not apply a host adapter on lookalike domains such as
  `chat.deepseek.com.example.org`.
- Do not fight a page indefinitely with a `MutationObserver` or timer loop.
- Do not make browser appearance depend on the external system browser.

## Constraints

- FloatDesk targets `net10.0-windows` with WPF and WebView2 package
  `1.0.4314.35`.
- Windows 10/11 x64 compatibility must be preserved.
- `CoreWebView2Profile.PreferredColorScheme` is available in the pinned SDK and
  supports `Auto`, `Light`, and `Dark`.
- The app uses an evergreen WebView2 runtime. A
  machine with an older runtime may not expose the preferred-color-scheme
  property; the app must continue without a crash.
- All AI windows intentionally share the same WebView2 user-data folder and
  default profile.
- The settings file is JSON with `SchemaVersion = 1`. Existing settings must
  remain loadable.
- `AppSettings` is a positional record. Adding a required constructor argument
  would force unrelated call-site changes and risk old-file compatibility.
- Existing settings updates merge edited fields against the latest settings
  snapshot so concurrent changes are not overwritten.

## Approaches Considered

### 1. Native WebView2 color scheme only

Set `PreferredColorScheme` from FloatDesk's effective theme and rely on each
site's `prefers-color-scheme` implementation.

Trade-off: smallest and safest implementation, but it does not solve sites such
as DeepSeek that do not consistently follow the native signal. It therefore
does not meet the requested outcome.

### 2. Native WebView2 color scheme plus exact-host adapters (selected)

Use WebView2's native preference as the baseline, then apply a small,
site-owned theme state for known hosts that need help. The first adapter is for
DeepSeek.

Trade-off: requires a small amount of per-site maintenance when a site changes
its own theme protocol, but it honors the site's runtime and CSS, works with the
existing shared profile, and avoids unsafe global recoloring. This is the
user-selected approach and the recommended one.

### 3. Global forced dark mode

Inject broad CSS or invert colors on every page.

Trade-off: immediately affects more sites, but breaks logos, images, charts,
syntax colors, focus states, and site controls. It violates the requirement to
prefer official site behavior and is rejected.

## Product Behavior

The AI settings page gets an `网页外观` field in the existing window-behavior
card. The field uses the standard settings combo-box style and contains:

- `跟随 FloatDesk`: use the effective FloatDesk theme.
- `浅色`: always request the site's light presentation.
- `深色`: always request the site's dark presentation.

The default is `跟随 FloatDesk`. The setting applies only to the embedded AI
browser and AI login popups. It does not alter FloatDesk's chrome or external
browsers.

When a page has no verified adapter and does not implement
`prefers-color-scheme`, it keeps its own appearance. The application must not
show an error for this normal limitation. Help text for the setting says that
some websites do not provide a light/dark mode.

Changing the setting while the AI browser is open updates the current page and
future navigations without requiring a restart. A changed app theme updates an
open browser when the browser is in `跟随 FloatDesk` mode.

## Technical Design

### Settings Model and Compatibility

Add a browser-theme enum, for example:

```csharp
public enum BrowserThemeMode { FollowApp, Light, Dark }
```

Add an init-only property to `AppSettings` rather than a new positional
constructor parameter:

```csharp
public BrowserThemeMode BrowserTheme { get; init; } =
    BrowserThemeMode.FollowApp;
```

This keeps every existing `new AppSettings(...)` call and every old JSON file
working. A missing `browserTheme` property deserializes as `FollowApp`.
`SettingsValidator` rejects undefined enum values. No schema-version bump is
needed because this is an additive optional field with a safe default.

Add `BrowserThemeIndex` to `SettingsViewModel`. It is loaded from settings and
merged in `BuildCandidate` exactly like the existing browser-window fields:
if the user did not edit it, the latest concurrently written value wins; if the
user edited it, the edited value wins.

### Effective Scheme Resolution

Introduce a pure, testable resolver in `FloatDesk.Core` with no WebView2
dependency:

```csharp
public enum BrowserColorScheme { Light, Dark }

public static BrowserColorScheme Resolve(
    BrowserThemeMode requested,
    AppTheme appTheme,
    bool systemUsesDarkTheme)
```

Rules:

- `Light` -> Light.
- `Dark` -> Dark.
- `FollowApp` + `AppTheme.Light` -> Light.
- `FollowApp` + `AppTheme.Dark` -> Dark.
- `FollowApp` + `AppTheme.System` -> System preference.

Expose the existing system-theme lookup from `ThemeService` so the app can pass
the current Windows app-theme value to the resolver without duplicating registry
logic. Under Windows high contrast, WebView2 and the OS accessibility system
remain authoritative; FloatDesk does not inject replacement colors.

### WebView2 Native Application

After `EnsureCoreWebView2Async` succeeds and before the first `Navigate`:

1. Resolve the effective scheme.
2. Set `CoreWebView2.Profile.PreferredColorScheme` to the corresponding
   `CoreWebView2PreferredColorScheme` value.
3. Install any matching host adapter's document-created script.
4. Only then perform the initial navigation.

`ApplySettings` stores the requested mode even when WebView2 is not initialized
yet. If initialized, it updates the profile property and re-applies the adapter
to the current document. If `PreferredColorScheme` throws
`NotImplementedException` or another supported API-unavailable exception, the
app records no fatal state and continues with the adapter path.

The AI login popup receives the resolved scheme and the same adapter
configuration. Because it uses the same WebView2 environment/profile, native
scheme changes apply to newly created pages automatically, but the popup still
sets the scheme explicitly after initialization and applies the adapter before
navigation. A long-lived popup is notified when the main browser's resolved
scheme changes.

### Adapter Contract

Adapters are selected by exact host after URI normalization (case-insensitive
host, optional explicit port, no suffix or wildcard matching). The top-level
document is the only target. Each adapter must:

- check the exact hostname again inside the injected script;
- use only a verified, site-owned DOM/theme mechanism;
- be idempotent for repeated calls and repeated document-created execution;
- stop after bounded initialization retries and never poll forever;
- avoid inspecting or transmitting page data;
- fail silently while leaving navigation functional;
- remove/undo only state that it owns when switching from Dark to Light;
- add no third-party-origin behavior.

The first adapter is `chat.deepseek.com`. Its current site code exposes
`data-ds-dark-theme` on the document root as its own dark-theme state. The
adapter treats the attribute as presence-based: it sets an empty
`data-ds-dark-theme` attribute for Dark and removes it for Light, while keeping
`document.documentElement.style.colorScheme` aligned. It targets the top-level
DeepSeek page only.

The adapter is installed with
`CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync` before navigation so
that it can run as soon as the document root exists. It is also executed after
`NavigationCompleted` as a recovery path. A small bounded set of initialization
retries handles the site's hydration without producing an indefinite observer
loop. When the requested scheme changes, the previous registered script is
removed and replaced with the script for the new scheme; the new script is also
executed immediately in the current page.

Unsupported hosts use native behavior only. There is no generic dark adapter.

### Lifecycle and Reapplication

- Initial browser creation: `ApplySettings` stores the mode, initialization
  sets the profile scheme and adapter script, then navigates.
- Setting change: `AppRuntime.ApplySettings` updates the existing
  `AiBrowserWindow`; it changes the profile scheme, refreshes registered
  document-created scripts, and applies the adapter to the current page.
- App theme change: the same path runs because `ApplySettings` receives the new
  `General.Theme`.
- Windows theme change: `AppRuntime` listens for `UserPreferenceChanged`. While
  the application theme is `System` or the browser is in `FollowApp`, it
  reapplies app resources and the resolved browser scheme. No-op updates avoid
  unnecessary page events.
- Navigation: the document-created script handles supported hosts before page
  paint where possible; `NavigationCompleted` reapplies the adapter if the page
  replaced the root state.
- Process recovery: browser reinitialization repeats the same setup before
  navigating.
- Popup: uses the shared environment, sets the profile scheme, installs the
  matching adapter, and navigates.

### Failure and Fallback Behavior

- Missing WebView2 runtime: existing unavailable-state UI remains unchanged.
- Older runtime without `PreferredColorScheme`: native theming degrades, while
  known adapters may still run; no crash and no settings loss.
- Adapter host mismatch or changed DOM: no global CSS fallback; the page keeps
  its own theme and remains usable.
- Navigation to an unknown host: the host-guarded adapter script performs no DOM
  changes.
- High contrast: OS accessibility colors win; no arbitrary palette override.
- A site's own post-load theme choice may override the adapter. FloatDesk
  reapplies on the next navigation or setting change but does not run a
  perpetual DOM battle.
- Website remembers its own theme: FloatDesk's selection remains the in-app
  preference; the next navigation reinjects it.

## Security and Privacy

- Adapter code only toggles document-root theme state and `color-scheme`.
- It must not use `localStorage`, cookies, credentials, network calls, or
  content extraction unless a future site integration is separately reviewed
  and documented.
- Existing host-object, web-message, permission, download, and HTTPS navigation
  policies remain unchanged.
- Adapter scripts run only for exact supported top-level hosts.

## Test Strategy

Automated tests:

- Old JSON without `browserTheme` loads as `FollowApp`.
- New settings round-trip the selected mode and validator rejects undefined
  values.
- Resolver matrix covers FollowApp with System/Light/Dark plus explicit
  Light/Dark.
- Exact-host adapter selection accepts `chat.deepseek.com` and rejects
  lookalike or suffix domains.
- Adapter script contains the exact host guard, top-level-page guard,
  idempotent marker, and expected dark/light state operations.
- Settings merge tests prove an untouched theme preserves a concurrent change
  and an edited theme wins.
- XAML tests verify the AI page exposes the three labels and the new selector.
- Theme resource tests continue to pass.

Manual release checks on Windows 10 and Windows 11 x64:

- Set FloatDesk Dark; launch the AI browser; DeepSeek is dark without a full
  light-page flash.
- Switch FloatDesk to Light while DeepSeek is open; the page becomes light
  without navigation.
- Select explicit Light/Dark while FloatDesk uses the opposite theme.
- Change Windows app theme while FloatDesk is `System`; both app and AI page
  follow when applicable.
- Open a DeepSeek login popup in both themes.
- Navigate to an unsupported host; confirm native behavior only and no errors.
- Reload, go back/forward, recover a failed process, and confirm the selected
  scheme persists.
- Inspect DevTools/script execution only in a test build; release keeps
  DevTools disabled.

## Acceptance Criteria

- The AI settings page contains the three-option appearance selector and saves
  it through the normal Apply action.
- Existing settings files load without error and use FollowApp by default.
- In FollowApp mode, changing FloatDesk's effective theme updates an open AI
  page without restart.
- Explicit Light/Dark override FloatDesk's theme for the AI page.
- DeepSeek switches using its own `data-ds-dark-theme` state, with no global CSS
  inversion.
- Unknown sites are not forcibly restyled.
- Login popups receive the same effective scheme and adapter configuration.
- All existing navigation, permissions, downloads, zoom, topmost, and release
  behavior remains unchanged.
- Release build has zero warnings/errors and the full automated test suite
  passes.

## Builder Check

The design was reviewed from the perspective of a fresh implementer. The
following implementation calls are intentional and do not change the approved
product behavior:

- Keep the effective-scheme resolver in `FloatDesk.Core`; keep WebView2 and
  JavaScript construction in `FloatDesk.App`.
- Use one host-guarded document-created adapter bundle rather than trying to
  guess the next host during navigation. This avoids a post-load flash when a
  later link returns to a supported host. Exact hostname checks inside the
  script preserve the host-scoped contract.
- Treat DeepSeek's dark state as an attribute-presence flag and set it to an
  empty string. `document.documentElement.style.colorScheme` is updated with
  the same resolved scheme.
- Keep bounded adapter retries to three moments: immediate, after one animation
  frame, and after 250 ms. No persistent observer is used.
- Dispatch `UserPreferenceChanged` work back to the WPF UI dispatcher and
  compute the resolved scheme before touching WebView2; this makes Windows
  theme changes safe and avoids redundant page events.
- Existing open login popups receive native profile updates automatically and
  receive adapter updates through the browser window that created them. A
  popup is unsubscribed when it closes.
- The setting remains SchemaVersion 1 with an init-only default. If a future
  release requires downgrade protection, that is a separate schema decision.