using System.Text.Json;

namespace DiaryOfMine.Infrastructure.Storage;

public sealed class ReminderAcknowledgementStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly object _gate = new();
    private Dictionary<string, DateTimeOffset>? _acknowledged;

    public ReminderAcknowledgementStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
            throw new ArgumentException("提醒确认记录必须使用绝对路径。", nameof(filePath));
        _filePath = Path.GetFullPath(filePath);
    }

    public bool IsAcknowledged(string reminderKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderKey);
        lock (_gate)
            return LoadCore().ContainsKey(reminderKey);
    }

    public void Acknowledge(string reminderKey, DateTimeOffset acknowledgedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderKey);
        lock (_gate)
        {
            var values = LoadCore();
            values[reminderKey] = acknowledgedAt;
            Prune(values, acknowledgedAt.AddDays(-90));
            SaveCore(values);
        }
    }

    private Dictionary<string, DateTimeOffset> LoadCore()
    {
        if (_acknowledged is not null) return _acknowledged;
        if (!File.Exists(_filePath))
            return _acknowledged = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        try
        {
            using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var envelope = JsonSerializer.Deserialize<Envelope>(stream, JsonOptions)
                ?? throw new InvalidDataException("提醒确认记录为空。");
            if (envelope.Version != 1)
                throw new InvalidDataException("不支持当前提醒确认记录版本。");
            return _acknowledged = new Dictionary<string, DateTimeOffset>(
                envelope.Acknowledged ?? new Dictionary<string, DateTimeOffset>(),
                StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("提醒确认记录格式损坏，原文件已保留。", exception);
        }
    }

    private void SaveCore(IReadOnlyDictionary<string, DateTimeOffset> values)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("提醒确认记录位置无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new Envelope(1, values), JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _filePath, overwrite: true);
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

    private static void Prune(
        IDictionary<string, DateTimeOffset> values,
        DateTimeOffset threshold)
    {
        foreach (var key in values
                     .Where(pair => pair.Value < threshold)
                     .Select(pair => pair.Key)
                     .ToArray())
            values.Remove(key);
    }

    private sealed record Envelope(
        int Version,
        IReadOnlyDictionary<string, DateTimeOffset>? Acknowledged);
}
