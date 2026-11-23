using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace WristMusic.Views;

public partial class CameraToolbox : UserControl
{
    public bool Wrist { get; set; }
    public ActionCommand OpenTool { get; }
    public ActionCommand CloseTool { get; }
    public ActionCommand PickFolder { get; }
    public ActionCommand AcceptFolder { get; }
    public ActionCommand CancelFolder { get; }
    public ActionCommand GoUp { get; }
    public ActionCommand PageFolders { get; }
    private readonly ActionCommand _enterFolder;
    private string? _directory;
    private string[] _entries = [];
    private int _page, _load;
    private const int Rows = 3;

    public CameraToolbox()
    {
        OpenTool = new(_ => Show(Form));
        CloseTool = new(_ => Show(Entry));
        PickFolder = new(_ => Pick());
        CancelFolder = new(_ => Show(Form));
        AcceptFolder = new(_ =>
        {
            if (_directory != null && DataContext is MainViewModel vm) vm.Recorder.ChooseFolder(_directory);
            Show(Form);
        });
        GoUp = new(_ => _ = Browse(_directory == null ? null : Path.GetDirectoryName(_directory)));
        PageFolders = new(p => { _page += p?.ToString() == "1" ? 1 : -1; DisplayRows(); });
        _enterFolder = new(p => _ = Browse(p as string));
        InitializeComponent();
        Form.IsVisibleChanged += (_, _) => RefreshDesktopPreview();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.Recorder.Preview.Watch(this, false);
            RefreshDesktopPreview();
        };
    }
    private void RefreshDesktopPreview()
    {
        if (!Wrist && DataContext is MainViewModel vm) vm.Recorder.Preview.Watch(this, Form.IsVisible);
    }
    private void Show(FrameworkElement active)
    {
        ++_load;
        foreach (var panel in new FrameworkElement[] { Entry, Form, FolderBrowser })
            panel.Visibility = panel == active ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Pick()
    {
        if (DataContext is not MainViewModel vm || !vm.Recorder.CanConfigure) return;
        if (!Wrist)
        {
            var dialog = new OpenFolderDialog { Title = "选择录制保存位置", InitialDirectory = vm.Recorder.Folder };
            if (dialog.ShowDialog() == true) vm.Recorder.ChooseFolder(dialog.FolderName);
            return;
        }
        Show(FolderBrowser);
        _ = Browse(vm.Recorder.Folder);
    }
    private async Task Browse(string? directory)
    {
        int load = ++_load;
        _directory = directory;
        _page = 0;
        _entries = [];
        DisplayRows();
        ChooseHere.IsEnabled = false;
        ParentFolder.IsEnabled = directory != null;
        BrowserPath.Text = directory ?? "此电脑";
        FolderMessage.Text = "正在读取…";
        try
        {
            var entries = await Task.Run(() => directory == null ? Directory.GetLogicalDrives() :
                Directory.GetDirectories(directory).Order(StringComparer.CurrentCultureIgnoreCase).ToArray());
            if (load != _load) return;
            _entries = entries;
            ChooseHere.IsEnabled = directory != null;
            FolderMessage.Text = entries.Length == 0 ? "没有子文件夹" : "";
            DisplayRows();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { if (load == _load) FolderMessage.Text = "无法读取此位置，请选择其他文件夹。"; }
    }
    private void DisplayRows()
    {
        DirectoryRows.Children.Clear();
        foreach (string path in _entries.Skip(_page * Rows).Take(Rows))
            DirectoryRows.Children.Add(new Button
            {
                Content = new TextBlock { Text = Path.GetFileName(path) is { Length: > 0 } name ? name : path, TextTrimming = TextTrimming.CharacterEllipsis },
                Command = _enterFolder, CommandParameter = path, HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 1, 0, 1)
            });
        Prev.IsEnabled = _page > 0;
        Next.IsEnabled = (_page + 1) * Rows < _entries.Length;
    }
}
