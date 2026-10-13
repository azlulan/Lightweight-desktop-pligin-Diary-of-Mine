using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiaryOfMine.Application.Abstractions;
using DiaryOfMine.Domain;

namespace DiaryOfMine.Infrastructure.Storage;

public sealed class EncryptedJsonCalendarRepository : ICalendarRepository, IDisposable
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Header = "DOMCAL01"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public EncryptedJsonCalendarRepository(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
            throw new ArgumentException("日程数据文件必须使用绝对路径。", nameof(filePath));
        _filePath = Path.GetFullPath(filePath);
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
            var all = await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false);
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
            var all = (await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = all.FindIndex(candidate => candidate.Id == item.Id);
            if (index >= 0) all[index] = item;
            else all.Add(item);
            await SaveAllCoreAsync(all, cancellationToken).ConfigureAwait(false);
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
            var all = (await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
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
            await SaveAllCoreAsync(all, cancellationToken).ConfigureAwait(false);
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
            var all = await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false);
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
            var all = (await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            all.RemoveAll(item => item.TaskBatch?.BatchId == batchId);
            all.AddRange(validated);
            await SaveAllCoreAsync(all, cancellationToken).ConfigureAwait(false);
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
            var all = (await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (all.RemoveAll(item => item.Id == id) > 0)
                await SaveAllCoreAsync(all, cancellationToken).ConfigureAwait(false);
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
            var all = (await LoadAllCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (all.RemoveAll(item => idSet.Contains(item.Id)) > 0)
                await SaveAllCoreAsync(all, cancellationToken).ConfigureAwait(false);
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
            await SaveAllCoreAsync(
                Array.Empty<CalendarItem>(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<CalendarItem>> LoadAllCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return Array.Empty<CalendarItem>();

        var payload = await File.ReadAllBytesAsync(_filePath, cancellationToken).ConfigureAwait(false);
        if (payload.Length <= Header.Length || !payload.AsSpan(0, Header.Length).SequenceEqual(Header))
            throw new InvalidDataException("日程数据文件头无效，已停止读取且不会覆盖原文件。");

        try
        {
            var plain = Unprotect(payload.AsSpan(Header.Length).ToArray());
            try
            {
                var envelope = JsonSerializer.Deserialize<CalendarEnvelope>(plain, JsonOptions)
                    ?? throw new InvalidDataException("日程数据为空。");
                if (envelope.Version != 1)
                    throw new InvalidDataException("暂不支持该日程数据版本。");
                return envelope.Items.Select(item => item.Validate()).ToArray();
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("日程数据无法解析，已保留原文件。", exception);
        }
    }

    private async Task SaveAllCoreAsync(
        IReadOnlyCollection<CalendarItem> items,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("日程数据目录无效。");
        Directory.CreateDirectory(directory);

        var plain = JsonSerializer.SerializeToUtf8Bytes(new CalendarEnvelope(1, items), JsonOptions);
        byte[]? protectedPayload = null;
        var temporaryPath = _filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            protectedPayload = Protect(plain);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await stream.WriteAsync(Header, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(protectedPayload, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath)) File.Replace(temporaryPath, _filePath, null);
            else File.Move(temporaryPath, _filePath);
        }
        finally
        {
            Array.Clear(plain);
            if (protectedPayload is not null) Array.Clear(protectedPayload);
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static bool Intersects(
        CalendarItem item,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive)
    {
        if (item.Kind == CalendarItemKind.Class && item.EndsAt is null)
            return item.StartsAt < toExclusive;
        if (item.EndsAt is null)
            return item.StartsAt >= fromInclusive && item.StartsAt < toExclusive;
        return item.StartsAt < toExclusive && item.EndsAt > fromInclusive;
    }

    private static byte[] Protect(byte[] plain) => TransformWithDpapi(plain, protect: true);
    private static byte[] Unprotect(byte[] encrypted) => TransformWithDpapi(encrypted, protect: false);

    private static byte[] TransformWithDpapi(byte[] input, bool protect)
    {
        var inputBlob = new DataBlob();
        var outputBlob = new DataBlob();
        try
        {
            inputBlob.Size = input.Length;
            inputBlob.Data = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, inputBlob.Data, input.Length);

            var succeeded = protect
                ? CryptProtectData(ref inputBlob, "DiaryOfMine calendar", IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref outputBlob);

            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Size);
            return result;
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero)
            {
                for (var index = 0; index < inputBlob.Size; index++) Marshal.WriteByte(inputBlob.Data, index, 0);
                Marshal.FreeHGlobal(inputBlob.Data);
            }
            if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private sealed record CalendarEnvelope(int Version, IReadOnlyCollection<CalendarItem> Items);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStructure,
        uint flags,
        ref DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStructure,
        uint flags,
        ref DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
