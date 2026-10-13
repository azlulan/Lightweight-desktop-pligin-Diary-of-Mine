using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiaryOfMine.Application.Diary;
using DiaryOfMine.Domain;
using Microsoft.Win32;

namespace DiaryOfMine.Desktop.Views;

public partial class DiaryEditorWindow : Window
{
    private readonly DateOnly _date;
    private readonly DiaryService _diaryService;
    private bool _saving;
    private bool _allowClose;
    private bool _closeRequested;
    private bool _hasPersistedEntry;

    internal DateOnly Date => _date;
    public event EventHandler? Saved;
    public event EventHandler? Deleted;
    internal event Action<DateOnly>? DayDetailRequested;

    public DiaryEditorWindow(DateOnly date, DiaryService diaryService, DiaryEntry? existing)
    {
        _date = date;
        _diaryService = diaryService ?? throw new ArgumentNullException(nameof(diaryService));
        _hasPersistedEntry = existing is not null;
        InitializeComponent();
        DateHeading.Text = date.ToString("yyyy年M月d日  dddd", CultureInfo.CurrentCulture);
        Title = $"{date:yyyy/M/d} · 日记";
        TitleTextBox.Text = existing?.Title ?? $"{date:M月d日}日记";
        if (!string.IsNullOrWhiteSpace(existing?.BodyPackageBase64))
            LoadDocument(existing.BodyPackageBase64);
        DataObject.AddPastingHandler(BodyEditor, OnBodyPasting);
        DeleteButton.IsEnabled = _hasPersistedEntry;
        BodyEditor.Focus();
    }

    internal async Task<bool> SaveAndCloseAsync()
    {
        _closeRequested = true;
        return await SaveAsync(closeAfterSave: true).ConfigureAwait(true);
    }

    internal void CloseAfterSavedNavigation()
    {
        _allowClose = true;
        Close();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e) =>
        await SaveAsync(closeAfterSave: false);

    private async void OnSaveAndExitClick(object sender, RoutedEventArgs e) =>
        await SaveAndCloseAsync();

    private async void OnOpenDayDetailClick(object sender, RoutedEventArgs e)
    {
        if (!await SaveAsync(closeAfterSave: false)) return;
        DayDetailRequested?.Invoke(_date);
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (!_hasPersistedEntry || _saving) return;
        using var autoFloatSuspension =
            ((App)System.Windows.Application.Current).SuspendAutoFloat();
        var result = System.Windows.MessageBox.Show(
            this,
            $"确定删除 {_date:yyyy年M月d日} 的整篇日记？\n\n此操作会删除正文和其中插入的图片，无法撤回。",
            "删除整篇日记",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        Activate();
        Focus();
        if (result != MessageBoxResult.Yes) return;

        _saving = true;
        DeleteButton.IsEnabled = false;
        StatusText.Text = "正在删除…";
        try
        {
            await _diaryService.DeleteAsync(_date).ConfigureAwait(true);
            _hasPersistedEntry = false;
            Deleted?.Invoke(this, EventArgs.Empty);
            var owner = Owner;
            _allowClose = true;
            Close();
            owner?.Dispatcher.BeginInvoke(() =>
            {
                if (!owner.IsVisible) return;
                owner.Activate();
                owner.Focus();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            StatusText.Text = "删除失败";
            DeleteButton.IsEnabled = true;
            System.Windows.MessageBox.Show(
                this, $"无法删除日记。\n\n{exception.Message}", "Diary of Mine",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _saving = false;
        }
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            await SaveAndCloseAsync();
            return;
        }
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await SaveAsync(closeAfterSave: false);
            return;
        }
    }

    private async Task<bool> SaveAsync(bool closeAfterSave)
    {
        if (_saving)
        {
            _closeRequested |= closeAfterSave;
            return false;
        }
        _saving = true;
        StatusText.Text = "正在保存…";
        try
        {
            var title = TitleTextBox.Text.Trim();
            if (title.Length == 0)
            {
                title = $"{_date:M月d日}日记";
                TitleTextBox.Text = title;
            }
            var bodyPackage = SaveDocument();
            var searchText = BuildSearchText();
            var preview = searchText.Length <= 240 ? searchText : searchText[..240];
            await _diaryService.SaveAsync(
                _date, title, bodyPackage, preview, searchText).ConfigureAwait(true);
            _hasPersistedEntry = true;
            DeleteButton.IsEnabled = true;
            StatusText.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
            Saved?.Invoke(this, EventArgs.Empty);
            if (closeAfterSave || _closeRequested)
            {
                _allowClose = true;
                Close();
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException)
        {
            StatusText.Text = "保存失败";
            System.Windows.MessageBox.Show(
                this, $"无法保存日记。\n\n{exception.Message}", "Diary of Mine",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            _saving = false;
        }
    }

    private string SaveDocument()
    {
        using var stream = new MemoryStream();
        var range = new TextRange(BodyEditor.Document.ContentStart, BodyEditor.Document.ContentEnd);
        range.Save(stream, DataFormats.XamlPackage);
        return Convert.ToBase64String(stream.ToArray());
    }

    private void LoadDocument(string bodyPackageBase64)
    {
        try
        {
            var bytes = Convert.FromBase64String(bodyPackageBase64);
            using var stream = new MemoryStream(bytes, writable: false);
            var range = new TextRange(BodyEditor.Document.ContentStart, BodyEditor.Document.ContentEnd);
            range.Load(stream, DataFormats.XamlPackage);
        }
        catch (Exception exception) when (exception is FormatException
            or ArgumentException
            or IOException)
        {
            StatusText.Text = "正文载入失败，原数据仍保留";
        }
    }

    private string BuildSearchText()
    {
        var text = new TextRange(
            BodyEditor.Document.ContentStart, BodyEditor.Document.ContentEnd).Text;
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 240 ? text : text[..240];
    }

    private void OnInsertImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要插入日记的图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var fileName in dialog.FileNames)
        {
            try { InsertImage(LoadBitmap(fileName)); }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                System.Windows.MessageBox.Show(
                    this, $"无法插入图片。\n\n{exception.Message}", "Diary of Mine",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void OnBodyPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Bitmap)
            && e.DataObject.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            e.CancelCommand();
            InsertImage(bitmap);
            return;
        }
        if (!e.DataObject.GetDataPresent(DataFormats.FileDrop)
            || e.DataObject.GetData(DataFormats.FileDrop) is not string[] files)
            return;
        var imageFiles = files.Where(IsImageFile).ToArray();
        if (imageFiles.Length == 0) return;
        e.CancelCommand();
        foreach (var file in imageFiles) InsertImage(LoadBitmap(file));
    }

    private void InsertImage(BitmapSource source)
    {
        var image = new Image
        {
            Source = source,
            Stretch = Stretch.Uniform,
            MaxWidth = 720,
            MaxHeight = 560,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var imageBlock = new BlockUIContainer(image)
        {
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 12, 0, 12)
        };
        var nextParagraph = new Paragraph();
        if (BodyEditor.CaretPosition.Paragraph is { Parent: FlowDocument } paragraph)
        {
            BodyEditor.Document.Blocks.InsertAfter(paragraph, imageBlock);
            BodyEditor.Document.Blocks.InsertAfter(imageBlock, nextParagraph);
        }
        else
        {
            BodyEditor.Document.Blocks.Add(imageBlock);
            BodyEditor.Document.Blocks.Add(nextParagraph);
        }
        BodyEditor.CaretPosition = nextParagraph.ContentStart;
        BodyEditor.Focus();
    }

    private static BitmapImage LoadBitmap(string fileName)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(fileName, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static bool IsImageFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        await SaveAndCloseAsync();
    }
}
