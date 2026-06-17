using GitHub.Copilot;
using Spectre.Console;

namespace CopilotSDK.Demos.Shared.Infrastructure;

/// <summary>
/// Kolorowy formatter zdarzeń sesji Copilot SDK.
///
/// SDK jest sterowany zdarzeniami: monit jest wysyłany raz, a następnie środowisko wykonawcze
/// emituje obiekty SessionEvent w trakcie myślenia asystenta, strumieniowania tekstu, wywołań
/// narzędzi, żądań uprawnień i ostatecznego przejścia w stan bezczynności. Ten rejestrator
/// celowo uwidacznia nazwy zdarzeń, dzięki czemu nowicjusze mogą powiązać wyjście konsoli
/// z modelem zdarzeń SDK.
/// </summary>
public static class EventLogger
{
    /// <summary>
    /// Subskrybuje wszystkie zdarzenia z jednej sesji <see cref="CopilotSession"/>.
    /// Zwolnij zwróconą subskrypcję, gdy sesja nie jest już obserwowana; w przeciwnym
    /// razie wywołanie zwrotne pozostanie zarejestrowane na czas życia sesji.
    /// </summary>
    public static IDisposable Attach(CopilotSession session, bool verbose = true)
    {
        // session.On<TEvent> to główny punkt obserwacyjny SDK. Użycie bazowego typu
        // SessionEvent pozwala temu rejestratorowi zobaczyć każde zdarzenie emitowane przez
        // środowisko wykonawcze, a nie tylko końcowe wiadomości asystenta.
        return session.On<SessionEvent>(evt => Log(evt, verbose));
    }

    public static void Log(SessionEvent evt, bool verbose = true)
    {
        switch (evt)
        {
            case AssistantMessageDeltaEvent delta:
                // Sesje strumieniowe emitują AssistantMessageDeltaEvent wiele razy
                // w trakcie jednej tury. Każda delta to tylko nowy fragment tekstu,
                // drukowany bez znaku nowej linii, aby zrekonstruować odpowiedź.
                if (verbose)
                    AnsiConsole.Markup($"[dim]{EscapeMarkup(delta.Data.DeltaContent)}[/]");
                break;

            case AssistantMessageEvent msg:
                // AssistantMessageEvent to ukończona wiadomość asystenta dla danej tury.
                // W sesjach niestrumieniowych może to być pierwsze zdarzenie zawierające
                // tekst asystenta; w sesjach strumieniowych potwierdza ono finalnie
                // złożoną treść.
                if (verbose)
                    AnsiConsole.MarkupLine($"\n[bold green]✓ ASSISTANT[/] [dim]({msg.Data.Content.Length} znaków)[/]");
                break;

            case AssistantReasoningDeltaEvent reasoningDelta:
                // Niektóre modele ujawniają osobne zdarzenia rozumowania. Nie są one
                // tekstem odpowiedzi widocznym dla użytkownika, więc wersje demonstracyjne
                // renderują je inaczej niż treść AssistantMessageDeltaEvent.
                if (verbose)
                    AnsiConsole.Markup($"[dim italic grey]{EscapeMarkup(reasoningDelta.Data.DeltaContent)}[/]");
                break;

            case AssistantReasoningEvent reasoning:
                AnsiConsole.MarkupLine($"[italic grey]💭 REASONING ({reasoning.Data.Content.Length} znaków)[/]");
                break;

            case ToolExecutionStartEvent toolStart:
                // ToolExecutionStartEvent oznacza, że model wybrał zarejestrowane
                // narzędzie/funkcję SDK i wygenerował dla niego argumenty JSON.
                // Aplikacja hosta nadal wykonuje to narzędzie lokalnie.
                AnsiConsole.MarkupLine(
                    $"[yellow]⚙ TOOL START[/] [bold]{EscapeMarkup(toolStart.Data.ToolName)}[/]" +
                    (verbose && toolStart.Data.Arguments is not null
                        ? $"\n   [dim]args: {EscapeMarkup(Truncate(toolStart.Data.Arguments.ToString() ?? "", 120))}[/]"
                        : ""));
                break;

            case ToolExecutionCompleteEvent toolDone:
                // ToolExecutionCompleteEvent przekazuje wynik po stronie hosta z powrotem
                // przez rozmowę SDK, aby model mógł kontynuować pracę z wyjściem
                // narzędzia w kontekście.
                var status = toolDone.Data.Success == true ? "[green]✓ TOOL DONE[/]" : "[red]✗ TOOL FAIL[/]";
                var resultStr = toolDone.Data.Result?.ToString() ?? "";
                AnsiConsole.MarkupLine(
                    $"{status} [bold]{EscapeMarkup(toolDone.Data.ToolCallId)}[/]" +
                    (verbose && !string.IsNullOrEmpty(resultStr)
                        ? $"\n   [dim]result: {EscapeMarkup(Truncate(resultStr, 120))}[/]"
                        : ""));
                break;

            case SessionIdleEvent:
                // SessionIdleEvent to sygnał SDK o zakończeniu tury. Wiele wersji
                // demonstracyjnych czeka na to zdarzenie przed odczytaniem finalnej
                // odpowiedzi lub zwróceniem strumienia HTTP.
                AnsiConsole.MarkupLine("[blue]◎ SESSION IDLE[/] — przetwarzanie zakończone");
                break;

            case SessionStartEvent start:
                // SessionStartEvent udostępnia metadane sesji przypisane przez środowisko
                // wykonawcze, w tym identyfikator sesji potrzebny w scenariuszach wznowienia/zdalnych.
                AnsiConsole.MarkupLine($"[bold blue]▶ SESSION START[/] id={EscapeMarkup(start.Data.SessionId ?? "(unknown)")} model={EscapeMarkup(start.Data.SelectedModel ?? "(default)")}");
                break;

            case SessionErrorEvent err:
                AnsiConsole.MarkupLine($"[bold red]✗ ERROR[/] {EscapeMarkup(err.Data.Message)}");
                break;

            case SessionCompactionStartEvent comp:
                AnsiConsole.MarkupLine($"[dim]⟳ COMPACTION START — {comp.Data.ConversationTokens} tokenów w rozmowie[/]");
                break;

            case SessionCompactionCompleteEvent comp:
                AnsiConsole.MarkupLine($"[dim]⟳ COMPACTION DONE — po: {comp.Data.PostCompactionTokens} tokenów[/]");
                break;

            case UserMessageEvent user:
                AnsiConsole.MarkupLine($"[cyan]→ USER[/] {EscapeMarkup(Truncate(user.Data.Content ?? "", 80))}");
                break;

            case PermissionRequestedEvent perm:
                // PermissionRequestedEvent jest emitowany, zanim środowisko wykonawcze
                // wykona akcję wymagającą zgody, taką jak wykonanie narzędzia lub zapis
                // do pamięci. PermissionHandler sesji decyduje o zezwoleniu/odmowie.
                AnsiConsole.MarkupLine(
                    $"[yellow]🔐 PERMISSION[/] kind={EscapeMarkup(perm.Data.PermissionRequest?.Kind ?? "?")}");
                break;
        }
    }

    private static string EscapeMarkup(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");

    private static string Truncate(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;
}
