using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;

namespace RWire.Tests;

/// <summary>
/// Wires NLog in as the ILogger backend for tests -
/// <c>RWire.csproj</c> itself stays logging-backend-agnostic (it only
/// depends on <c>Microsoft.Extensions.Logging.Abstractions</c> and
/// takes an <c>ILogger&lt;ProcessSupervisor&gt;</c> via an optional
/// constructor parameter). Writes to both the console and a per-run
/// log file - the file is specifically useful for investigating the
/// timing-sensitive restart tests after a run finishes, rather than
/// only watching console output live while it scrolls past.
/// </summary>
internal static class TestLogging
{
    private static readonly Lazy<ILoggerFactory> FactoryLazy = new(CreateFactory);

    public static ILogger<ProcessSupervisor> CreateProcessSupervisorLogger() =>
        FactoryLazy.Value.CreateLogger<ProcessSupervisor>();

    private static ILoggerFactory CreateFactory()
    {
        var config = new NLog.Config.LoggingConfiguration();

        var consoleTarget = new NLog.Targets.ConsoleTarget("console")
        {
            Layout = "${time} [${level:uppercase=true}] ${logger:shortName=true}: ${message} ${exception:format=tostring}",
        };

        string logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);
        var fileTarget = new NLog.Targets.FileTarget("file")
        {
            FileName = Path.Combine(logDirectory, "rwire-tests.log"),
            Layout = "${longdate} [${level:uppercase=true}] ${logger}: ${message} ${exception:format=tostring}",
            DeleteOldFileOnStartup = true,
        };

        config.AddTarget(consoleTarget);
        config.AddTarget(fileTarget);
        config.AddRule(NLog.LogLevel.Debug, NLog.LogLevel.Fatal, consoleTarget);
        config.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, fileTarget);

        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddNLog(config);
        });
    }
}
