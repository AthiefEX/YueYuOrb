# FloatDesk Architecture

FloatDesk targets Windows 10 21H2/22H2 and Windows 11 from a single x64
desktop binary. The application runs in the user's interactive session and
is not a Windows service.

The implementation separates four responsibilities:

- `FloatDesk.Core` contains models, pure policy, geometry, and interfaces.
- `FloatDesk.Windows` owns all Win32 interop and adapters.
- `FloatDesk.Infrastructure` owns JSON settings, SQLite notes, and logs.
- `FloatDesk.App` owns WPF windows, composition, and user interaction.

Only `WindowCoordinator` changes visibility caused by capture. One
`InputLockEngine` consumes foreground snapshots, and settings are serialized
through one coordinator. These boundaries prevent independent timers and
window handlers from conflicting.

The floating orb is a transparent WPF window. AI browsing uses an on-demand,
opaque WebView2 window because WPF and WebView2 use different native child
window surfaces. Product-wide topmost settings never become window owners, so
tool windows do not inherit orb z-order.

The application supports the normal, composed Windows desktop. Protected
content, elevated targets, exclusive-fullscreen games, and IME implementations
that reject layout changes are reported honestly and never represented as
successful through simulated state.
