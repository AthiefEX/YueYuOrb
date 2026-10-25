using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public partial class NotesWindow : Window, IDisposable
{
    private readonly INoteRepository _repository;
    private readonly DispatcherTimer _saveTimer = new();
    private readonly DispatcherTimer _searchTimer = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private NoteDocument? _current;
    private bool _editorLoading;
    private bool _recycleBin;
    private bool _dirty;
    private long _editRevision;

    public NotesWindow(INoteRepository repository)
    {
        InitializeComponent();
        _repository = repository;
        _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveCurrentAsync(showFailure: true);
        };
        _searchTimer.Interval = TimeSpan.FromMilliseconds(200);
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await RefreshListAsync();
        };
        Closing += OnClosing;
        Loaded += async (_, _) => await RefreshListAsync();
    }

    public bool HasUnsavedChanges => _dirty;

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        _saveTimer.Stop();
        await SaveCurrentAsync(showFailure: false, cancellationToken);
    }

    public async Task ShowAndActivateAsync()
    {
        if (!IsVisible)
            Show();
        Activate();
        await RefreshListAsync();
    }

    private async Task RefreshListAsync()
    {
        if (_dirty && !await SaveCurrentAsync(showFailure: true))
            return;
        var selectedId = _current?.Id;
        IReadOnlyList<NoteSummary> items;
        try
        {
            items = await _repository.SearchAsync(
                SearchBox.Text.Trim(),
                _recycleBin,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            SaveStatusText.Text = $"读取笔记失败：{exception.Message}";
            return;
        }
        NotesList.ItemsSource = items;
        var selected = items.FirstOrDefault(item => item.Id == selectedId)
            ?? (items.Count > 0 ? items[0] : null);
        NotesList.SelectedItem = selected;
        DeleteButton.Content = _recycleBin ? "恢复笔记" : "移到回收站";
        if (selected is null)
        {
            _current = null;
            SetEditor(new NoteDocument(
                Guid.Empty,
                "",
                "",
                0,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null));
            SaveStatusText.Text = _recycleBin ? "回收站为空。" : "暂无笔记，点击新建。";
        }
    }

    private async void OnNoteSelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (NotesList.SelectedItem is not NoteSummary summary ||
            summary.Id == _current?.Id)
        {
            return;
        }
        if (!await SaveCurrentAsync(showFailure: true))
        {
            RestoreCurrentSelection();
            return;
        }
        try
        {
            var note = await _repository.GetAsync(summary.Id, CancellationToken.None);
            if (note is not null)
                SetEditor(note);
            else
                RestoreCurrentSelection();
        }
        catch (Exception exception)
        {
            SaveStatusText.Text = $"读取笔记失败：{exception.Message}";
            RestoreCurrentSelection();
        }
    }

    private void RestoreCurrentSelection()
    {
        if (_current is null)
            return;
        NotesList.SelectedItem = NotesList.Items
            .OfType<NoteSummary>()
            .FirstOrDefault(item => item.Id == _current.Id);
    }

    private void SetEditor(NoteDocument note)
    {
        _editorLoading = true;
        _current = note;
        TitleBox.Text = note.Title;
        BodyBox.Text = note.Body;
        _dirty = false;
        _editRevision = note.Revision;
        _editorLoading = false;
        SaveStatusText.Text = note.Id == Guid.Empty ? "点击新建开始记录。" : "已保存。";
    }

    private void OnSearchTextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        if (!await SaveCurrentAsync(showFailure: true))
            return;
        if (_recycleBin)
            SetRecycleMode(false);
        var now = DateTimeOffset.UtcNow;
        var note = new NoteDocument(
            Guid.NewGuid(),
            "未命名笔记",
            "",
            1,
            now,
            now,
            null);
        var result = await _repository.SaveAsync(note, CancellationToken.None);
        if (!result.Success)
        {
            SaveStatusText.Text = result.Message ?? "无法新建笔记。";
            return;
        }
        await RefreshListAsync();
        _current = note;
        SetEditor(note);
        NotesList.SelectedItem = NotesList.Items
            .OfType<NoteSummary>()
            .FirstOrDefault(item => item.Id == note.Id);
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private async void OnRecycleClick(object sender, RoutedEventArgs e)
    {
        if (!await SaveCurrentAsync(showFailure: true))
            return;
        SetRecycleMode(!_recycleBin);
        await RefreshListAsync();
    }

    private void SetRecycleMode(bool enabled)
    {
        _recycleBin = enabled;
        RecycleButton.Content = enabled ? "返回笔记" : "回收站";
        DeleteButton.Content = enabled ? "恢复笔记" : "移到回收站";
    }

    private void OnEditorChanged(object sender, RoutedEventArgs e)
    {
        if (_editorLoading || _current is null || _current.Id == Guid.Empty)
            return;
        _dirty = true;
        _editRevision++;
        SaveStatusText.Text = "正在编辑，500 毫秒后自动保存…";
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task<bool> SaveCurrentAsync(
        bool showFailure,
        CancellationToken cancellationToken = default)
    {
        if (!_dirty || _current is null || _current.Id == Guid.Empty)
            return true;
        var revisionAtStart = _editRevision;
        var title = TitleBox.Text.Trim();
        var body = BodyBox.Text;
        if (string.IsNullOrEmpty(title))
            title = "未命名笔记";
        var candidate = _current with
        {
            Title = title,
            Body = body,
            Revision = revisionAtStart,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        SaveStatusText.Text = "正在保存…";
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var result = await _repository.SaveAsync(candidate, cancellationToken);
            if (!result.Success)
            {
                if (showFailure)
                    SaveStatusText.Text =
                        $"保存失败：{result.Message ?? result.ErrorCode}；正文仍保留在窗口中。";
                return false;
            }
            _current = candidate;
            if (_editRevision == revisionAtStart)
            {
                _dirty = false;
                SaveStatusText.Text = $"已保存 · {DateTime.Now:HH:mm:ss}";
            }
            else
            {
                _saveTimer.Stop();
                _saveTimer.Start();
                SaveStatusText.Text = "较早版本已保存，正在保存最新编辑…";
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (showFailure)
                SaveStatusText.Text = $"保存失败：{exception.Message}；正文仍保留。";
            return false;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async void OnSaveNowClick(object sender, RoutedEventArgs e)
    {
        _saveTimer.Stop();
        await SaveCurrentAsync(showFailure: true);
    }

    private async void OnRetrySaveClick(object sender, RoutedEventArgs e)
    {
        _dirty = true;
        _saveTimer.Stop();
        await SaveCurrentAsync(showFailure: true);
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var message = _recycleBin
            ? "恢复这条笔记？"
            : "将这条笔记移到回收站？正文不会立即删除。";
        if (MessageBox.Show(
                this,
                message,
                "FloatDesk 记事本",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        await MoveCurrentToRecycleAsync();
    }

    private async Task<bool> MoveCurrentToRecycleAsync()
    {
        if (_current is null || _current.Id == Guid.Empty)
            return false;
        if (!await SaveCurrentAsync(showFailure: true))
            return false;
        var result = await _repository.SetDeletedAsync(
            _current.Id,
            !_recycleBin,
            CancellationToken.None);
        if (!result.Success)
        {
            SaveStatusText.Text = result.Message ?? "操作失败。";
            return false;
        }
        _current = null;
        _dirty = false;
        await RefreshListAsync();
        return true;
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (_current is null)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = SanitizeFileName(
                string.IsNullOrWhiteSpace(TitleBox.Text) ? "FloatDesk 笔记" : TitleBox.Text) +
                ".txt",
            Filter = "文本文件|*.txt",
            AddExtension = true,
            DefaultExt = ".txt",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            File.WriteAllText(
                dialog.FileName,
                $"{TitleBox.Text}{Environment.NewLine}{Environment.NewLine}{BodyBox.Text}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            SaveStatusText.Text = $"已导出：{dialog.FileName}";
        }
        catch (Exception exception)
        {
            SaveStatusText.Text = $"导出失败：{exception.Message}";
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        _saveTimer.Stop();
        var saved = await SaveCurrentAsync(showFailure: true);
        if (!saved && _dirty)
        {
            var answer = MessageBox.Show(
                this,
                "笔记尚未保存。要另存为文本后关闭，还是返回继续处理？\n\n是：另存为文本\n否：返回",
                "笔记尚未保存",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
                OnExportClick(this, new RoutedEventArgs());
            Show();
            Activate();
            return;
        }
        Hide();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(character =>
            invalid.Contains(character) ? '_' : character).ToArray();
        var result = new string(chars).Trim();
        return result.Length == 0 ? "FloatDesk 笔记" : result;
    }

    public void Dispose()
    {
        _saveGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
