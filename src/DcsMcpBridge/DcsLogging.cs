using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Compact;

/// <summary>
/// The app's persistent log: structured JSON lines (CLEF) rolled daily into
/// <c>dcs-aiautomator-yyyyMMdd.clef</c>, with files older than the retention period deleted.
/// Exposed as a plain <see cref="ILoggerProvider"/> so the libraries stay Serilog-agnostic — the
/// app passes <see cref="Provider"/> to <see cref="DcsMcpBridgeHost.StartAsync"/> and uses it for
/// its own loggers too, so one file tells the whole story. Configured in code only:
/// Serilog.Settings.Configuration is reflection-based and fights AOT/trimming.
/// </summary>
public sealed class DcsLogging : IDisposable
{
    public const string FileNamePrefix = "dcs-aiautomator-";
    private const long MaxFileSizeBytes = 10 * 1024 * 1024;

    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly Logger _logger;

    /// <param name="logDirectory">Resolved by the app (packaged local cache folder); never read
    /// from ApplicationData here, since tests run unpackaged.</param>
    /// <param name="retentionDays">Days to keep log files. Read once at startup.</param>
    public DcsLogging(string logDirectory, LogLevel minimumLevel, int retentionDays)
    {
        LogDirectory = logDirectory;
        _levelSwitch = new LoggingLevelSwitch(ToSerilog(minimumLevel));
        _logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_levelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(logDirectory, FileNamePrefix + ".clef"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: MaxFileSizeBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: null, // retention is by age, not file count
                retainedFileTimeLimit: TimeSpan.FromDays(Math.Max(1, retentionDays)))
            .CreateLogger();
        Provider = new SerilogLoggerProvider(_logger, dispose: false);
    }

    public string LogDirectory { get; }

    /// <summary>Pass to DcsMcpBridgeHost.StartAsync and use for app-side loggers.</summary>
    public ILoggerProvider Provider { get; }

    /// <summary>Takes effect immediately, for every logger created from <see cref="Provider"/>.</summary>
    public void SetMinimumLevel(LogLevel level) => _levelSwitch.MinimumLevel = ToSerilog(level);

    /// <summary>Flushes and closes the file. Call on app exit, after the bridge host is disposed.</summary>
    public void Dispose()
    {
        Provider.Dispose();
        _logger.Dispose();
    }

    private static LogEventLevel ToSerilog(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Information => LogEventLevel.Information,
        LogLevel.Warning => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        _ => LogEventLevel.Fatal,
    };
}
