using GitHub.Copilot;
using GitHub.Copilot.Rpc;

namespace CopilotSDK.Demos.Shared.Infrastructure;

/// <summary>
/// Metody pomocnicze do pracy z <see cref="CopilotSession"/>.
///
/// CopilotSession to obiekt SDK reprezentujący jeden wątek rozmowy: jest właścicielem
/// wyboru modelu, komunikatu systemowego, narzędzi, serwerów MCP, uprawnień oraz
/// skumulowanej historii rozmowy dla tego wątku. Ci pomocnicy opakowują sterowane
/// zdarzeniami elementy SDK we wzorce używane wielokrotnie w wersjach demonstracyjnych.
/// </summary>
public static class SessionHelper
{
    /// <summary>
    /// Wysyła monit i czeka, aż SDK zgłosi <see cref="SessionIdleEvent"/>.
    ///
    /// Copilot SDK oddziela „wyślij wiadomość” od „obserwuj odpowiedź”: SendAsync
    /// rozpoczyna turę, podczas gdy session.On odbiera zdarzenia w miarę jej postępu.
    /// Ten pomocnik łączy oba elementy, gdy demo wymaga prostego kształtu
    /// żądanie/odpowiedź.
    /// </summary>
    public static async Task<string> SendAndWaitAsync(
        CopilotSession session,
        string prompt,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        var lastMessage = string.Empty;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutValue = timeout.GetValueOrDefault();
        using var timeoutCts = timeout.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;
        if (timeoutCts is not null)
            timeoutCts.CancelAfter(timeoutValue);

        var effectiveCancellationToken = timeoutCts?.Token ?? cancellationToken;

        using var sub = session.On<SessionEvent>(evt =>
        {
            // Przechwyć ostatnią wiadomość asystenta i rozstrzygnij, gdy środowisko wykonawcze
            // zgłosi bezczynność sesji. Delty strumieniowe celowo nie są tu gromadzone,
            // ponieważ AssistantMessageEvent przenosi finalną, pełną treść tury.
            switch (evt)
            {
                case AssistantMessageEvent msg:
                    lastMessage = msg.Data.Content;
                    break;
                case SessionIdleEvent:
                    done.TrySetResult();
                    break;
                case SessionErrorEvent err:
                    done.TrySetException(new InvalidOperationException(err.Data.Message));
                    break;
            }
        });

        using var registration = effectiveCancellationToken.Register(() => done.TrySetCanceled(effectiveCancellationToken));

        try
        {
            // MessageOptions to koperta SDK dla tury użytkownika. Monit jest wysyłany do
            // sesji skonfigurowanej wcześniej z modelem, narzędziami, serwerami MCP
            // oraz obsługą uprawnień.
            await session.SendAsync(new MessageOptions { Prompt = prompt }, effectiveCancellationToken);
            await done.Task.WaitAsync(effectiveCancellationToken);
            return lastMessage;
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested && timeoutCts?.IsCancellationRequested == true && timeout.HasValue)
        {
            throw new TimeoutException(
                $"Copilot session did not become idle within {timeoutValue:g}. " +
                "Retry the demo or check Copilot authentication and network connectivity.",
                ex);
        }
    }

    /// <summary>
    /// Czeka na określony typ zdarzenia SDK.
    ///
    /// Wersje demonstracyjne używają tego, gdy potrzebują kamienia milowego cyklu życia,
    /// np. SessionStartEvent, aby poznać identyfikator sesji przypisany w czasie wykonywania,
    /// zanim wyświetlą instrukcje wznowienia.
    /// </summary>
    public static async Task<T> WaitForEventAsync<T>(
        CopilotSession session,
        Func<T, bool>? predicate = null,
        TimeSpan? timeout = null) where T : SessionEvent
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? sub = null;
        sub = session.On<SessionEvent>(evt =>
        {
            // Subskrybowanie SessionEvent i filtrowanie lokalnie utrzymuje elastyczność
            // pomocnika: można oczekiwać dowolnego typu zdarzenia SDK jednym pomocnikiem.
            if (evt is T typed && (predicate?.Invoke(typed) ?? true))
            {
                tcs.TrySetResult(typed);
            }
        });

        try
        {
            return await tcs.Task.WaitAsync(timeout ?? TimeSpan.FromMinutes(5));
        }
        finally
        {
            sub?.Dispose();
        }
    }

    /// <summary>
    /// Procedura obsługi uprawnień, która rejestruje każde żądanie uprawnień SDK i je zatwierdza.
    ///
    /// Procedury obsługi uprawnień są częścią SessionConfig. Środowisko wykonawcze wywołuje
    /// je przed operacjami wymagającymi zgody, a aplikacja hosta zwraca PermissionDecision
    /// zezwalającą na operację, odrzucającą ją lub dostosowującą.
    /// </summary>
#pragma warning disable GHCP001
    public static Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> LoggingApproveAll(
        Action<string>? logAction = null)
#pragma warning restore GHCP001
    {
        return (request, invocation) =>
        {
            // PermissionInvocation identyfikuje sesję Copilot, która wywołała żądanie,
            // co jest przydatne, gdy jeden klient obsługuje wiele sesji.
            logAction?.Invoke($"[PERMISSION] kind={request.Kind}, session={invocation.SessionId}");
            return PermissionHandler.ApproveAll(request, invocation);
        };
    }
}
