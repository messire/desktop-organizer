using System.IO;
using System.Windows;

namespace DesktopOrganizer.App;

/// <summary>
/// Interaction logic for MainWindow.xaml.
/// </summary>
public partial class MainWindow : Window
{
    private readonly List<FileItem> _fileItems = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        LoadFileList();
    }

    /// <inheritdoc />
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveFileList();
        base.OnClosing(e);
    }

    private void DeleteFile_Click(object sender, RoutedEventArgs e)
    {
        if (FileListBox.SelectedItem is not FileItem selectedItem) return;

        _fileItems.Remove(selectedItem);
        RefreshFileList();
    }

    private void FileListBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Delete && FileListBox.SelectedItem is FileItem selectedItem)
        {
            _fileItems.Remove(selectedItem);
            RefreshFileList();
        }
    }

    private void Window_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    // Событие при отпускании файла на окно
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        foreach (string file in files)
        {
            if (!File.Exists(file)) continue;
            if (_fileItems.Exists(item => item.FilePath == file)) continue;

            _fileItems.Add(new FileItem { FilePath = file });
        }

        RefreshFileList();
    }

    private void RefreshFileList()
    {
        FileListBox.Items.Clear();
        foreach (var item in _fileItems)
        {
            FileListBox.Items.Add(new
            {
                item.FileName,
                Icon = FileIconHelper.GetFileIcon(item.FilePath),
            });
        }

        FileCountTextBlock.Text = $"Количество файлов: {_fileItems.Count}";
    }

    private void SaveFileList()
    {
        var filePaths = string.Join("\n", _fileItems.ConvertAll(item => item.FilePath));
        Properties.Settings.Default.SavedFiles = filePaths;
        Properties.Settings.Default.Save();
    }

    private void LoadFileList()
    {
        var savedFiles = Properties.Settings.Default.SavedFiles;
        if (string.IsNullOrWhiteSpace(savedFiles)) return;
        var splitted = savedFiles.Split(['\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var filePath in splitted)
        {
            if (!File.Exists(filePath)) continue;
            _fileItems.Add(new FileItem { FilePath = filePath });
        }

        RefreshFileList();
    }
}
