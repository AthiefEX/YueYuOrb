namespace FloatDesk.App;

public sealed class AppDataPaths
{
    public AppDataPaths()
    {
        Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FloatDesk");
        Assets = Path.Combine(Root, "assets");
        LauncherIcons = Path.Combine(Root, "launcher-icons");
        NotesDatabase = Path.Combine(Root, "data", "notes.db");
        WebViewData = Path.Combine(Root, "WebView2");
        Logs = Path.Combine(Root, "logs");
        LockFile = Path.Combine(Root, "app.lock");
    }

    public string Root { get; }
    public string Assets { get; }
    public string LauncherIcons { get; }
    public string NotesDatabase { get; }
    public string WebViewData { get; }
    public string Logs { get; }
    public string LockFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Assets);
        Directory.CreateDirectory(LauncherIcons);
        Directory.CreateDirectory(Path.GetDirectoryName(NotesDatabase)!);
        Directory.CreateDirectory(WebViewData);
        Directory.CreateDirectory(Logs);
    }
}
