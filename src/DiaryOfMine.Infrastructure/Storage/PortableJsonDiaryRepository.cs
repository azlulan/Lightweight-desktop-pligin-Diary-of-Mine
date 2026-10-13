using System.Text.Json;
using DiaryOfMine.Application.Abstractions;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Infrastructure.Storage;

public sealed class PortableJsonDiaryRepository : IDiaryRepository, IDisposable
{
    public const string FileName = "DiaryOfMine.diary.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _filePath;
    private bool _disposed;

    public PortableJsonDiaryRepository(string filePath) =>
        _filePath = NormalizeFilePath(filePath);

    public async Task<IReadOnlyList<DiaryEntry>> GetAllAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadAsync(cancellationToken).ConfigureAwait(false))
                .OrderByDescending(entry => entry.Date)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DiaryEntry?> GetAsync(DateOnly date, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return (await LoadAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(entry => entry.Date == date);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DiaryEntry entry, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);
        entry = entry.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = entries.FindIndex(candidate => candidate.Date == entry.Date);
            if (index >= 0) entries[index] = entry;
            else entries.Add(entry);
            await SaveCoreAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(DateOnly date, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = (await LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (entries.RemoveAll(entry => entry.Date == date) == 0) return;
            await SaveCoreAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SwitchDirectoryAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("日记缓存位置必须是绝对路径。", nameof(directory));
        var targetPath = NormalizeFilePath(Path.Combine(directory, FileName));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(targetPath, _filePath, StringComparison.OrdinalIgnoreCase)) return;
            var current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var previousPath = _filePath;
            try
            {
                _filePath = targetPath;
                if (File.Exists(targetPath))
                    _ = await LoadAsync(cancellationToken).ConfigureAwait(false);
                else
                    await SaveCoreAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _filePath = previousPath;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<DiaryEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return Array.Empty<DiaryEntry>();
        try
        {
            await using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var envelope = await JsonSerializer.DeserializeAsync<DiaryEnvelope>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("日记数据文件为空。");
            if (envelope.Version != 1)
                throw new InvalidDataException("暂不支持该日记数据文件版本。");
            return (envelope.Entries ?? Array.Empty<DiaryEntry>())
                .Select(entry => entry.Validate()).ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("日记数据文件无法解析，原文件已保留。", exception);
        }
    }

    private async Task SaveCoreAsync(
        IReadOnlyCollection<DiaryEntry> entries,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("日记缓存位置无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, new DiaryEnvelope(1, entries), JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_filePath)) File.Replace(temporaryPath, _filePath, null);
            else File.Move(temporaryPath, _filePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string NormalizeFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("日记数据文件必须使用绝对路径。", nameof(path));
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private sealed record DiaryEnvelope(int Version, IReadOnlyCollection<DiaryEntry>? Entries);
}
