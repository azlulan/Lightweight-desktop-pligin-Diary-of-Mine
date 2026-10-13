using System.Globalization;
using System.Text;
using DiaryOfMine.Application.Logging;

namespace DiaryOfMine.Infrastructure.Logging;

public sealed class FileAppLogger : IAppLogger
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private readonly object _writeLock = new();
    private string _directory;

    public FileAppLogger(string directory)
    {
        _directory = NormalizeDirectory(directory);
        Directory.CreateDirectory(_directory);
    }

    public void UpdateDirectory(string directory)
    {
        var normalized = NormalizeDirectory(directory);
        Directory.CreateDirectory(normalized);
        lock (_writeLock)
            _directory = normalized;
    }

    public ILogOperation BeginOperation(string eventName)
    {
        ValidateToken(eventName, nameof(eventName));
        var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        TryWrite("输入", eventName, operationId, null, null);
        return new FileLogOperation(this, eventName, operationId);
    }

    private void TryWrite(
        string direction,
        string eventName,
        string operationId,
        string? result,
        Exception? exception)
    {
        var timestamp = DateTimeOffset.Now;
        var line = new StringBuilder(192)
            .Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" | ").Append(direction)
            .Append(" | event=").Append(eventName)
            .Append(" | id=").Append(operationId);

        if (result is not null)
            line.Append(" | result=").Append(result);
        if (exception is not null)
        {
            line.Append(" | error=").Append(exception.GetType().FullName ?? exception.GetType().Name)
                .Append(" | hresult=0x")
                .Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture));
            if (exception.TargetSite is { } targetSite)
            {
                line.Append(" | site=")
                    .Append(targetSite.DeclaringType?.FullName ?? "unknown")
                    .Append('.').Append(targetSite.Name);
            }
        }
        line.AppendLine();

        try
        {
            lock (_writeLock)
            {
                var dateDirectory = Path.Combine(
                    _directory, timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(dateDirectory);
                var logFile = Path.Combine(dateDirectory, "DiaryOfMine.txt");
                using var stream = new FileStream(
                    logFile,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.WriteThrough);
                using var writer = new StreamWriter(stream, Utf8WithoutBom);
                writer.Write(line);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
        }
        catch (IOException)
        {
            // 日志故障不能造成递归日志，也不能中断用户操作。
        }
        catch (UnauthorizedAccessException)
        {
            // 不提升权限，不回退到其他目录。
        }
    }

    private static void ValidateToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 80
            || value.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '.' or '-' or '_')))
            throw new ArgumentException("日志事件名只能包含字母、数字、点、横线和下划线。", parameterName);
    }

    private static string NormalizeResult(string result) =>
        !string.IsNullOrWhiteSpace(result)
        && result.Length <= 32
        && result.All(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_')
            ? result
            : "invalid-result";

    private static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("日志目录必须是完整的绝对路径。", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private sealed class FileLogOperation : ILogOperation
    {
        private readonly FileAppLogger _logger;
        private readonly string _eventName;
        private readonly string _operationId;
        private int _completed;

        public FileLogOperation(FileAppLogger logger, string eventName, string operationId)
        {
            _logger = logger;
            _eventName = eventName;
            _operationId = operationId;
        }

        public void Complete(string result = "success")
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _logger.TryWrite(
                "输出", _eventName, _operationId, NormalizeResult(result), null);
        }

        public void Fail(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _logger.TryWrite("输出", _eventName, _operationId, "failure", exception);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _logger.TryWrite("输出", _eventName, _operationId, "incomplete", null);
        }
    }
}
