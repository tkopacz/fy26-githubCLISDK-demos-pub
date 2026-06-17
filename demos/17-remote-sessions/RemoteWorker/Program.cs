using GitHub.Copilot;
using Microsoft.Extensions.AI;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

ConsoleRenderer.Banner("Remote Worker", "Demo 17 — GitHub Copilot SDK: start work on the server, then disconnect");

var handshake = await CopilotClientFactory.LoadRemoteRuntimeHandshakeAsync();
if (handshake is null)
{
    ConsoleRenderer.Error("Brak aktywnego pliku handshake runtime albo handshake wygasł. Uruchom najpierw RemoteServer.");
    return 1;
}

if (args.Contains("--resume"))
{
    return await ResumeAndReportAsync(handshake);
}

return await StartAndDisconnectAsync(handshake);

async Task<int> StartAndDisconnectAsync(CopilotClientFactory.RemoteRuntimeHandshake handshake)
{
    var model = CopilotClientFactory.GetModelId("gpt-5.4-mini");
    var provider = CopilotClientFactory.GetByokProvider();
    // To narzędzie działa w procesie hosta, który jest właścicielem połączenia sesji, ale
    // sama sesja jest rejestrowana w zdalnym środowisku wykonawczym Copilot. Widocznym
    // efektem jest długotrwała rozmowa po stronie serwera, która może przetrwać
    // odłączenie klienta roboczego.
    var longCalculationTool = AIFunctionFactory.Create(
        (int iterations, int startValue, int step) =>
        {
            var current = startValue;
            for (var i = 0; i < iterations; i++)
            {
                current = (current * 13 + step + i) % 1_000_003;
                Thread.Sleep(250);
            }

            return $"calculation_done iterations={iterations} start={startValue} step={step} current={current}";
        },
        new AIFunctionFactoryOptions
        {
            Name = "run_long_calculation",
            Description = "Runs a deterministic long calculation on the server runtime to demonstrate remote session processing.",
        });

    // CreateRemoteClient łączy ten proces z serwerem wykonawczym uruchomionym przez
    // RemoteServer, zamiast uruchamiać oddzielne lokalne środowisko wykonawcze SDK.
    await using var client = CopilotClientFactory.CreateRemoteClient($"{handshake.Host}:{handshake.Port}", handshake.ConnectionToken);
    await client.StartAsync();

    // Sesja jest tworzona w zdalnym środowisku wykonawczym. Klient roboczy może się rozłączyć
    // po rozpoczęciu tury, a inny klient może ją wznowić według SessionId.
    await using var session = await client.CreateSessionAsync(new SessionConfig
    {
        Model = model,
        Provider = provider,
        OnPermissionRequest = PermissionHandler.ApproveAll,
        Tools = [longCalculationTool],
        SystemMessage = new SystemMessageConfig
        {
            Mode = SystemMessageMode.Replace,
            Content = "You are running a long calculation on a remote Copilot runtime. Use the tool run_long_calculation to compute a deterministic result. Return the final summary in a few sentences, and explicitly say that the session is persisted on the server.",
        },
    });

    using var eventLog = EventLogger.Attach(session, verbose: false);
    var sessionStarted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var subscription = session.On<SessionEvent>(evt =>
    {
        // SessionStartEvent dostarcza wymagany identyfikator sesji przypisany przez środowisko
        // wykonawcze, aby później wznowić tę zdalną rozmowę.
        if (evt is SessionStartEvent start && !string.IsNullOrWhiteSpace(start.Data.SessionId))
            sessionStarted.TrySetResult(start.Data.SessionId);

        if (evt is ToolExecutionStartEvent tool)
            ConsoleRenderer.Warn($"Tool started on server: {tool.Data.ToolName}");
    });

    var prompt = "Use the run_long_calculation tool with iterations=18, startValue=7, step=11, then summarize the result. Explain that this is a server-side calculation and the client can disconnect now.";
    // Celowo nie czekamy tutaj na pełną turę. SendAsync rozpoczyna pracę w zdalnym
    // środowisku wykonawczym; demo czeka tylko na SessionStartEvent, zachowuje
    // SessionId i kończy działanie, aby pokazać scenariusz wznowienia po rozłączeniu.
    _ = session.SendAsync(new MessageOptions { Prompt = prompt });

    var sessionId = await sessionStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
    ConsoleRenderer.Success($"Session started on server: {sessionId}");

    // Utrwala wznawialny uchwyt sesji SDK. Monit i token połączenia nie są zapisywane;
    // tylko identyfikator sesji/URL środowiska wykonawczego wymagany przez --resume.
    var sessionStateFile = await CopilotClientFactory.SaveRemoteSessionStateAsync(new CopilotClientFactory.RemoteSessionState(
        SessionId: sessionId,
        RuntimeUrl: $"{handshake.Host}:{handshake.Port}",
        StartedAtUtc: DateTimeOffset.UtcNow,
        ExpiresAtUtc: handshake.ExpiresAtUtc));
    ConsoleRenderer.Info($"Session state file: {sessionStateFile}");

    ConsoleRenderer.Info("Czekam chwilę, aby obliczenie zaczęło działać na serwerze...");
    await Task.Delay(TimeSpan.FromSeconds(2));
    ConsoleRenderer.Warn("Rozłączam klienta. Sesja powinna nadal działać po stronie runtime.");

    return 0;
}

async Task<int> ResumeAndReportAsync(CopilotClientFactory.RemoteRuntimeHandshake handshake)
{
    var sessionState = await CopilotClientFactory.LoadRemoteSessionStateAsync();
    if (sessionState is null)
    {
        ConsoleRenderer.Error("Brak aktywnej zapisanej sesji do wznowienia albo stan wygasł. Najpierw uruchom RemoteWorker bez --resume.");
        return 1;
    }

    var model = CopilotClientFactory.GetModelId("gpt-5.4-mini");
    var provider = CopilotClientFactory.GetByokProvider();
    // Narzędzia muszą zostać dostarczone ponownie po wznowieniu, ponieważ delegaty żyją
    // w procesie hosta; SDK przywraca historię rozmowy, a nie obiekty funkcji .NET.
    var longCalculationTool = AIFunctionFactory.Create(
        (int iterations, int startValue, int step) =>
        {
            var current = startValue;
            for (var i = 0; i < iterations; i++)
            {
                current = (current * 13 + step + i) % 1_000_003;
                Thread.Sleep(250);
            }

            return $"calculation_done iterations={iterations} start={startValue} step={step} current={current}";
        },
        new AIFunctionFactoryOptions
        {
            Name = "run_long_calculation",
            Description = "Runs a deterministic long calculation on the server runtime to demonstrate remote session processing.",
        });

    // Dołącz nowy proces klienta do tego samego zdalnego środowiska wykonawczego.
    await using var client = CopilotClientFactory.CreateRemoteClient(sessionState.RuntimeUrl, handshake.ConnectionToken);
    await client.StartAsync();

    // ResumeSessionAsync ponownie łączy się z istniejącą zdalną rozmową według identyfikatora.
    // Model może kontynuować z poprzednich tur, nawet jeśli jest to nowy proces klienta.
    await using var session = await client.ResumeSessionAsync(sessionState.SessionId, new ResumeSessionConfig
    {
        Model = model,
        Provider = provider,
        OnPermissionRequest = PermissionHandler.ApproveAll,
        Tools = [longCalculationTool],
    });

    using var _ = EventLogger.Attach(session, verbose: false);
    // W tej turze wznowiona sesja prosi o podsumowanie wszystkiego, co zostało ukończone,
    // gdy pierwszy klient roboczy był odłączony.
    var result = await SessionHelper.SendAndWaitAsync(session,
        "Resume the existing remote session, inspect the completed calculation result, and summarize it clearly for the user.");

    ConsoleRenderer.Success("Sesja wznowiona po rozłączeniu klienta.");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Wynik z serwera:[/]");
    AnsiConsole.WriteLine(result);
    CopilotClientFactory.DeleteRemoteSessionState();

    return 0;
}
