using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DiaryOfMine.Application.Diary;
using DiaryOfMine.Application.Logging;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Desktop.ViewModels;

public sealed class LinksPageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DiaryService _diaryService;
    private readonly IAppLogger _logger;
    private IReadOnlyList<DiaryEntry> _allEntries = Array.Empty<DiaryEntry>();
    private bool _loaded;
    private bool _disposed;
    private bool _isLoading;
    private string _statusMessage = string.Empty;
    private string _searchText = string.Empty;

    public ObservableCollection<DiaryCardViewModel> Entries { get; } = new();
    public bool HasEntries => Entries.Count > 0;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (string.Equals(_searchText, value, StringComparison.Ordinal)) return;
            _searchText = value ?? string.Empty;
            OnPropertyChanged();
            ApplyFilter();
        }
    }

    public string EmptyStateTitle => string.IsNullOrWhiteSpace(SearchText)
        ? "还没有写下第一页"
        : "没有找到匹配的日记";

    public string EmptyStateDescription => string.IsNullOrWhiteSpace(SearchText)
        ? "从今天开始，留住文字、图片与一段安静的时间。"
        : "试试标题、正文关键词，或 2026、0911 这样的日期数字。";

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public event Action<DateOnly>? DiaryOpenRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    public LinksPageViewModel(DiaryService diaryService, IAppLogger logger)
    {
        _diaryService = diaryService ?? throw new ArgumentNullException(nameof(diaryService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InitializeAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task RefreshAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var operation = _logger.BeginOperation("diary.list.load");
        IsLoading = true;
        try
        {
            var entries = await _diaryService.GetAllAsync().ConfigureAwait(true);
            _allEntries = entries;
            ApplyFilter();
            operation.Complete(entries.Count == 0 ? "empty" : $"loaded-{entries.Count}");
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            StatusMessage = $"无法读取日记：{exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Open(DateOnly date)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DiaryOpenRequested?.Invoke(date);
    }

    private void ApplyFilter()
    {
        if (_disposed) return;
        var tokens = SearchText.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IReadOnlyList<DiaryEntry> filtered = tokens.Length == 0
            ? _allEntries
            : _allEntries.Where(entry => Matches(entry, tokens)).ToArray();
        Entries.Clear();
        foreach (var entry in filtered)
            Entries.Add(new DiaryCardViewModel(entry));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDescription));
        StatusMessage = tokens.Length == 0
            ? (_allEntries.Count == 0 ? "还没有日记。" : $"共 {_allEntries.Count} 篇日记")
            : $"找到 {filtered.Count} 篇日记";
    }

    private static bool Matches(DiaryEntry entry, IReadOnlyList<string> tokens)
    {
        var dateText = $"{entry.Date.Day}/{entry.Date.Month}/{entry.Date.Year} "
            + $"{entry.Date:yyyyMMdd} {entry.Date:MMdd} "
            + $"{entry.Date.Year}年{entry.Date.Month}月{entry.Date.Day}日";
        var haystack = $"{entry.Title}\n{entry.SearchText}\n{entry.PreviewText}\n{dateText}";
        return tokens.All(token => haystack.Contains(token, StringComparison.CurrentCultureIgnoreCase));
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DiaryOpenRequested = null;
        PropertyChanged = null;
    }
}

public sealed class DiaryCardViewModel
{
    public DateOnly Date { get; }
    public string DateHeading { get; }
    public string PreviewText { get; }
    public string UpdatedText { get; }

    public DiaryCardViewModel(DiaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Date = entry.Date;
        DateHeading = $"{entry.Date.Day}/{entry.Date.Month}/{entry.Date.Year}/{entry.Title}";
        PreviewText = string.IsNullOrWhiteSpace(entry.PreviewText)
            ? "这页日记还没有正文。"
            : entry.PreviewText;
        UpdatedText = $"更新于 {entry.UpdatedAt.LocalDateTime:HH:mm}";
    }
}
