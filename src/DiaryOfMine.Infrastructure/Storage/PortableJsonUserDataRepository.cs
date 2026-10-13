using System.Text.Json;
using System.Text.Json.Serialization;
using DiaryOfMine.Application.Abstractions;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Infrastructure.Storage;

public sealed class PortableJsonUserDataRepository : ICalendarRepository, IDisposable
{
    public const string FileName = "DiaryOfMine.userdata.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _legacyFilePath;
    private string _filePath;
    private bool _legacyMigrationAttempted;
    private bool _disposed;

    public string FilePath => _filePath;

    public PortableJsonUserDataRepository(string filePath, string? legacyFilePath = null)
    {
        _filePath = NormalizeFilePath(filePath);
        _legacyFilePath = string.IsNullOrWhiteSpace(legacyFilePath)
            ? null
            : NormalizeFilePath(legacyFilePath);
    }

    public async Task<IReadOnlyList<CalendarItem>> GetRangeAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
            return all
                .Where(item => Intersects(item, fromInclusive, toExclusive))
                .OrderBy(item => item.StartsAt)
                .ThenBy(item => item.CreatedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(CalendarItem item, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(item);
        item = item.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await LoadCurrentAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = all.FindIndex(candidate => candidate.Id == item.Id);
            if (index >= 0) all[index] = item;
            else all.Add(item);
            await SaveToAsync(_filePath, all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveManyAsync(
        IReadOnlyCollection<CalendarItem> items,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return;
        var validated = items.Select(item => item.Validate()).ToArray();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await LoadCurrentAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var indices = all.Select((item, index) => (item.Id, index))
                .ToDictionary(pair => pair.Id, pair => pair.index);
            foreach (var item in validated)
            {
                if (indices.TryGetValue(item.Id, out var index)) all[index] = item;
                else
                {
                    indices[item.Id] = all.Count;
                    all.Add(item);
                }
            }
            await SaveToAsync(_filePath, all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CalendarItem>> GetTaskBatchAsync(
        Guid batchId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (batchId == Guid.Empty) throw new ArgumentException("Task 批次标识不能为空。", nameof(batchId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
            return all.Where(item => item.TaskBatch?.BatchId == batchId)
                .OrderBy(item => item.StartsAt)
                .ThenBy(item => item.CreatedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceTaskBatchAsync(
        Guid batchId,
        IReadOnlyCollection<CalendarItem> items,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(items);
        if (batchId == Guid.Empty) throw new ArgumentException("Task 批次标识不能为空。", nameof(batchId));
        var validated = items.Select(item => item.Validate()).ToArray();
        if (validated.Any(item => item.TaskBatch?.BatchId != batchId))
            throw new ArgumentException("替换内容必须全部属于同一 Task 批次。", nameof(items));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await LoadCurrentAsync(cancellationToken).ConfigureAwait(false)).ToList();
            all.RemoveAll(item => item.TaskBatch?.BatchId == batchId);
            all.AddRange(validated);
            await SaveToAsync(_filePath, all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await LoadCurrentAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (all.RemoveAll(item => item.Id == id) > 0)
                await SaveToAsync(_filePath, all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return;
        var idSet = ids.ToHashSet();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = (await LoadCurrentAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (all.RemoveAll(item => idSet.Contains(item.Id)) > 0)
                await SaveToAsync(_filePath, all, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveToAsync(
                _filePath, Array.Empty<CalendarItem>(), cancellationToken).ConfigureAwait(false);
            _legacyMigrationAttempted = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SwitchDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("用户数据缓存位置必须是绝对路径。", nameof(directory));
        var targetPath = NormalizeFilePath(Path.Combine(directory, FileName));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(targetPath, _filePath, StringComparison.OrdinalIgnoreCase)) return;
            var currentItems = await LoadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (File.Exists(targetPath))
            {
                _ = await LoadFromAsync(targetPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SaveToAsync(targetPath, currentItems, cancellationToken).ConfigureAwait(false);
            }
            _filePath = targetPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<CalendarItem>> LoadCurrentAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_filePath))
            return await LoadFromAsync(_filePath, cancellationToken).ConfigureAwait(false);

        if (!_legacyMigrationAttempted && _legacyFilePath is not null && File.Exists(_legacyFilePath))
        {
            _legacyMigrationAttempted = true;
            using var legacy = new EncryptedJsonCalendarRepository(_legacyFilePath);
            var items = await legacy.GetRangeAsync(
                DateTimeOffset.MinValue, DateTimeOffset.MaxValue, cancellationToken).ConfigureAwait(false);
            await SaveToAsync(_filePath, items, cancellationToken).ConfigureAwait(false);
            return items;
        }

        return Array.Empty<CalendarItem>();
    }

    private static async Task<IReadOnlyList<CalendarItem>> LoadFromAsync(
        string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var envelope = await JsonSerializer.DeserializeAsync<UserDataEnvelope>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("用户数据文件为空。");
            if (envelope.Version != 1)
                throw new InvalidDataException("暂不支持该用户数据文件版本。");
            return (envelope.CalendarItems ?? Array.Empty<CalendarItem>())
                .Select(item => item.Validate()).ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("用户数据文件无法解析，原文件已保留。", exception);
        }
    }

    private static async Task SaveToAsync(
        string path, IReadOnlyCollection<CalendarItem> items, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("用户数据缓存位置无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, new UserDataEnvelope(1, items), JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
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

    private static bool Intersects(
        CalendarItem item, DateTimeOffset fromInclusive, DateTimeOffset toExclusive)
    {
        if (item.Kind == CalendarItemKind.Class && item.EndsAt is null)
            return item.StartsAt < toExclusive;
        if (item.EndsAt is null)
            return item.StartsAt >= fromInclusive && item.StartsAt < toExclusive;
        return item.StartsAt < toExclusive && item.EndsAt > fromInclusive;
    }

    private static string NormalizeFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("用户数据文件必须使用绝对路径。", nameof(path));
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private sealed record UserDataEnvelope(
        int Version,
        IReadOnlyCollection<CalendarItem>? CalendarItems);
}
