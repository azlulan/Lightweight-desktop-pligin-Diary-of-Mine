using System.Text.Json;
using DiaryOfMine.Application.Abstractions;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Infrastructure.Storage;

public sealed class PortableJsonWorkModeRepository : IWorkModeRepository, IDisposable
{
    public const string FileName = "DiaryOfMine.work.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _filePath;
    private bool _disposed;

    public PortableJsonWorkModeRepository(string filePath) => _filePath = Normalize(filePath);

    public async Task<WorkModeState> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(WorkModeState state, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(state, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SwitchDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("工作模式缓存位置必须是绝对路径。", nameof(directory));
        var target = Normalize(Path.Combine(directory, FileName));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(target, _filePath, StringComparison.OrdinalIgnoreCase)) return;
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var previous = _filePath;
            try
            {
                _filePath = target;
                if (File.Exists(target)) _ = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
                else await SaveCoreAsync(current, cancellationToken).ConfigureAwait(false);
            }
            catch { _filePath = previous; throw; }
        }
        finally { _gate.Release(); }
    }

    private async Task<WorkModeState> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return WorkModeState.Empty;
        try
        {
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var envelope = await JsonSerializer.DeserializeAsync<Envelope>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("工作模式数据文件为空。");
            if (envelope.Version != 1)
                throw new InvalidDataException("暂不支持该工作模式数据版本。");
            var state = envelope.State ?? WorkModeState.Empty;
            return state with
            {
                Goals = state.Goals ?? Array.Empty<WorkGoal>(),
                Sessions = state.Sessions ?? Array.Empty<WorkSession>(),
                Interruptions = state.Interruptions ?? Array.Empty<WorkInterruption>()
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("工作模式数据文件无法解析，原文件已保留。", exception);
        }
    }

    private async Task SaveCoreAsync(WorkModeState state, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("工作模式缓存位置无效。");
        Directory.CreateDirectory(directory);
        var temporary = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new Envelope(1, state),
                    JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            if (File.Exists(_filePath)) File.Replace(temporary, _filePath, null);
            else File.Move(temporary, _filePath);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("工作模式数据文件必须使用绝对路径。", nameof(path));
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private sealed record Envelope(int Version, WorkModeState State);
}
