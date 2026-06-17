// Demo 05 — PermissionHooks: Bezpieczny agent refaktoryzujący
//
// Pokazuje: OnPermissionRequest (custom handler) + SessionHooks.OnPreToolUse/OnPostToolUse.
// Agent może pisać do *.cs, ale polityka BLOKUJE write/shell na podstawie Kind.
// Każda decyzja logowana — audit trail gotowy do compliance.
// Uruchomienie: dotnet run --project demos/05-permission-hooks/PermissionHooks

using System.ComponentModel;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using CopilotSDK.Demos.PermissionHooks;
using CopilotSDK.Demos.Shared.Infrastructure;
using CopilotSDK.Demos.Shared.Rendering;
using Spectre.Console;

ConsoleRenderer.Banner("Safe Agent", "Demo 05 — GitHub Copilot SDK: Permission Hooks");
ConsoleRenderer.Info("Agent może refaktoryzować .cs, ale polityka blokuje shell i nieautoryzowany write.\n");

// Audit log decyzji (w produkcji: zapis do pliku/bazy)
var auditLog = new List<(string Kind, string Decision, string Reason, DateTimeOffset Time)>();
var workspaceRoot = PermissionHooksPolicy.NormalizeWorkspaceRoot(Directory.GetCurrentDirectory());

ConsoleRenderer.Info($"Workspace root: {workspaceRoot}\n");

void AddAudit(string kind, string decision, string reason)
{
    lock (auditLog)
        auditLog.Add((kind, decision, reason, DateTimeOffset.UtcNow));

    var color = decision == "Approved" ? "green" : "red";
    var icon = decision == "Approved" ? "✓" : "✗";
    AnsiConsole.MarkupLine(
        $"  [{color}]{icon} PERMISSION[/] kind=[bold]{kind.Replace("[", "[[").Replace("]", "]]")}[/] → {decision}: {reason.Replace("[", "[[").Replace("]", "]]")}");
}

// === Custom PermissionHandler ===
// OnPermissionRequest to pierwsza bramka autoryzacyjna SDK. Środowisko wykonawcze wywołuje
// tego delegata, gdy chce wykonać operację z uprawnieniami, przekazując PermissionRequest,
// który nazywa operację, oraz PermissionInvocation.
// Ważne — używane, gdy nie ma interaktywnego użytkownika.
#pragma warning disable GHCP001
Task<PermissionDecision> PermissionPolicyAsync(PermissionRequest request, PermissionInvocation _)
{
  var kind = request.Kind?.ToString() ?? string.Empty;
    var allowed = PermissionHooksPolicy.IsPermissionKindAllowed(kind);
    AddAudit(kind, allowed ? "Approved" : "Denied",
        allowed ? "Known safe demo tool; path is checked by PreToolUse" : "Shell or unknown tool denied by default");

    return Task.FromResult(allowed
        // ApproveOnce zezwala tylko na to konkretne żądanie SDK. Nie przyznaje
        // ogólnej zgody na przyszłe wywołania narzędzi w tej samej sesji.
        ? PermissionDecision.ApproveOnce()
        // UserNotAvailable jest tutaj używane jako decyzja odmowna: host odmawia
        // autoryzacji powłoki lub nieznanych operacji bez pytania operatora.
        : PermissionDecision.UserNotAvailable());
}
#pragma warning restore GHCP001

await using var client = CopilotClientFactory.Create();
await using var session = await client.CreateSessionAsync(new SessionConfig
{
    Model = CopilotClientFactory.GetModelId(),
    Provider = CopilotClientFactory.GetByokProvider(),
    // Delegat uprawnień jest dołączony tylko do tej sesji. Inna CopilotSession
    // z tego samego klienta może używać innych zasad.
    OnPermissionRequest = PermissionPolicyAsync,
    // SessionHooks to wywołania zwrotne SDK dotyczące wykonywania narzędzi. Są bardziej
    // szczegółowe niż OnPermissionRequest, ponieważ zawierają nazwę/argumenty narzędzia i
    // mogą zwrócić dodatkowy kontekst, który model zobaczy w rozmowie.
    Hooks = new SessionHooks
    {
        OnPreToolUse = async (input, _) =>
        {
            // PreToolUse jest uruchamiane po wybraniu narzędzia przez model i wygenerowaniu
            // argumentów, ale zanim host uruchomi to narzędzie. To właściwe miejsce na
            // walidację ścieżki i sprawdzanie zasad dotyczących skutków ubocznych.
            AnsiConsole.MarkupLine(
                $"[dim]  🔍 PreToolUse: {input.ToolName.Replace("[", "[[").Replace("]", "]]" )}[/]");

            var decision = PermissionHooksPolicy.EvaluatePreToolUse(
                input.ToolName,
                input.ToolArgs?.ToString(),
                workspaceRoot);
            if (!decision.Allowed)
            {
                AddAudit(input.ToolName, "Denied", decision.Reason);
                AnsiConsole.MarkupLine($"[red]🚫 PreToolUse BLOCK: {decision.Reason.Replace("[", "[[").Replace("]", "]]")}[/]");
                return new PreToolUseHookOutput
                {
                    // Zwrócenie „deny” informuje SDK, aby nie uruchamiał narzędzia.
                    // AdditionalContext jest przekazywany agentowi, aby mógł
                    // wyjaśnić blokadę lub wybrać bezpieczniejszą alternatywę.
                    PermissionDecision = "deny",
                    AdditionalContext = $"BLOCKED: {decision.Reason}",
                };
            }

            // Zwrócenie „allow” pozwala SDK kontynuować wywołanie narzędzia po tym, jak
            // polityka zbadała konkretne argumenty.
            AddAudit(input.ToolName, "Approved", decision.Reason);
            return new PreToolUseHookOutput { PermissionDecision = "allow" };

            // Zapyta np. użytkownika!
            //return new PreToolUseHookOutput { PermissionDecision = "ask" };
        },

        OnPostToolUse = async (input, _) =>
        {
            // PostToolUse działa po wykonaniu po stronie hosta. Nie może cofnąć
            // efektu ubocznego, ale może dodać kontekst audytu do rozmowy i
            // zapisać, na co właśnie zezwolił SDK.
            AddAudit("tool", "Approved", $"PostToolUse completed for {input.ToolName}");
            AnsiConsole.MarkupLine(
                $"[dim]  ✓ PostToolUse: {input.ToolName.Replace("[", "[[").Replace("]", "]]" )}[/]");
            return new PostToolUseHookOutput
            {
                // AdditionalContext przekazuje modelowi zwięzłą notatkę od hosta o wyniku
                // wywołania narzędzia, wykraczającą poza surowe dane wyjściowe narzędzia.
                AdditionalContext = $"Audit: {input.ToolName} at {DateTimeOffset.UtcNow:HH:mm:ss}",
            };
        },
    },
    // Niestandardowe narzędzie tylko do odczytu udostępniane przez SDK. Daje modelowi
    // bezpieczny sposób na odnajdywanie celów refaktoryzacji bez przyznawania dowolnego
    // dostępu do odczytu systemu plików.
    Tools =
    [
        AIFunctionFactory.Create(
            ([Description("Ignored for safety; files are always listed from the configured workspace root.")] string directory = ".") =>
                PermissionHooksPolicy.ListCSharpFiles(workspaceRoot),
            new AIFunctionFactoryOptions
            {
                Name = "list_csharp_files",
                Description = "Lists C# files under the configured workspace root that could be refactored",
            }),
    ],
});

AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Zadanie dla agenta");

var task = """
You are a refactoring assistant. Please:
1. List the C# files in the current directory using list_csharp_files
2. Try to edit 'appsettings.json' to add a new "FeatureFlags" section (this WILL be blocked by PreToolUse hook)
3. Try to create a new file 'Refactored.cs' with a simple C# class (this should succeed)
4. Summarize: what were you able to do, and what was blocked by the permission policy?
""";

AnsiConsole.MarkupLine($"[dim]{task.Replace("[", "[[").Replace("]", "]]")}[/]\n");

var result = await ConsoleRenderer.SpinnerAsync(
    "Agent pracuje (obserwuj decyzje uprawnień)...",
    // Monit celowo prosi zarówno o dozwolone, jak i zablokowane operacje, więc
    // demonstracja pokazuje, jak Copilot reaguje, gdy hooki uprawnień SDK odmówią narzędzia.
    () => SessionHelper.SendAndWaitAsync(session, task));

AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Wynik agenta");
AnsiConsole.WriteLine(result);

AnsiConsole.WriteLine();
ConsoleRenderer.Rule("Audit Log");
ConsoleRenderer.Table(
    auditLog,
    ("Czas", e => e.Time.ToString("HH:mm:ss")),
    ("Kind", e => e.Kind),
    ("Decyzja", e => e.Decision),
    ("Powód", e => e.Reason.Length > 50 ? e.Reason[..50] + "…" : e.Reason));
