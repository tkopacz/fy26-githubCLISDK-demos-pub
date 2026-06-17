using CopilotSDK.Demos.Shared.Rendering;

namespace CopilotSDK.Demos.StreamingEvents;

internal sealed record CliOptions(bool Interactive, bool ShowHelp)
{
    public static CliOptions Parse(string[] args)
    {
        var interactive = false;
        var showHelp = false;

        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--interactive":
                case "-i":
                    interactive = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
            }
        }

        return new CliOptions(interactive, showHelp);
    }
}

internal static class StreamingEventsCliHelper
{
    internal static string GetSampleGitLog() => """
        abc1234 fix: correct null check in payment processor
        def5678 feat: add retry policy to HTTP client with Polly
        ghi9012 chore: bump System.Text.Json to 8.0.5
        jkl3456 fix: resolve race condition in session manager
        mno7890 feat: implement circuit breaker pattern
        pqr1234 refactor: extract IOrderRepository interface
        stu5678 test: add integration tests for AuthController
        vwx9012 fix: SQL injection in UserRepository.GetByUsername
        yza3456 feat: add OpenTelemetry distributed tracing
        bcd7890 docs: update API documentation for v2 endpoints
        """;

    internal static string ResolveGitLog(bool interactive, string? input)
    {
        if (interactive && !string.IsNullOrWhiteSpace(input))
            return input;

        return GetSampleGitLog();
    }

    internal static void PrintUsage()
    {
        ConsoleRenderer.Info("Użycie: dotnet run --project demos/02-streaming-events/StreamingEvents [--interactive]");
        ConsoleRenderer.Info("  --interactive, -i  wczytaj git log z stdin zamiast używać przykładowych commitów");
    }
}
