using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EasyTolet.Shared.Exceptions;

/// <summary>One JSON record per line in a UTC daily .txt file. Registered as a singleton.</summary>
public sealed class DailyTextExceptionLogSink(IOptions<ExceptionLoggingOptions> options,
    IHostEnvironment environment) : IExceptionLogSink, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WriteAsync(ExceptionLogEntry entry, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(options.Value.Directory, environment.ContentRootPath);
        var directory = Path.Combine(root, SafeSegment(entry.Service), SafeSegment(entry.Instance));
        var path = Path.Combine(directory, $"exceptions-{entry.OccurredAtUtc:yyyy-MM-dd}.txt");
        var record = JsonSerializer.Serialize(entry) + System.Environment.NewLine;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(directory);
            else System.IO.Directory.CreateDirectory(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read,
                Options = FileOptions.Asynchronous
            };
            if (!OperatingSystem.IsWindows())
                fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var stream = new FileStream(path, fileOptions);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(record), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string SafeSegment(string value) =>
        string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'))
            .Replace("..", "__");

    public void Dispose() => _gate.Dispose();
}
