using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Server.Hosting;

/// <summary>Logging for the one-shot maintenance commands: warnings and errors only, one line each.</summary>
public static class ConsoleLogging
{
    public static ILoggerFactory Quiet() =>
        LoggerFactory.Create(logging => logging.AddSimpleConsole(console => console.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
}
