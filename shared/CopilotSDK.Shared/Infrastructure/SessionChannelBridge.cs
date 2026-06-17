using System.Threading.Channels;
using GitHub.Copilot;

namespace CopilotSDK.Demos.Shared.Infrastructure;

/// <summary>
/// Pomost pomiędzy sterowanym zdarzeniami API Copilot SDK a
/// <see cref="IAsyncEnumerable{T}"/>.
///
/// CopilotSession.On wywołuje wywołania zwrotne za każdym razem, gdy środowisko wykonawcze
/// emituje zdarzenie SessionEvent. Strumieniowe punkty końcowe ASP.NET naturalnie udostępniają
/// jednak IAsyncEnumerable. Kanał (Channel) daje nam bezpieczny wątkowo punkt przekazania:
/// wywołania zwrotne zdarzeń SDK zapisują tekst asystenta w kanale, a kod odpowiedzi HTTP
/// odczytuje go we własnym tempie.
/// </summary>
public sealed class SessionChannelBridge : IDisposable
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly object _gate = new();
    private IDisposable? _subscription;
    private bool _disposed;

    public void Attach(CopilotSession session) =>
        // Przeciążenie publiczne akceptuje prawdziwą sesję SDK. AttachCore sprawia, że
        // zachowanie między zdarzeniami a kanałem jest testowalne bez uruchamiania środowiska wykonawczego.
        AttachCore(handler => session.On(handler));

    // Szew testowy: akceptuje dowolne źródło subskrypcji SessionEvent bez mockowania
    // CopilotSession ani uruchamiania środowiska wykonawczego Copilot.
    internal void AttachCore(Func<Action<SessionEvent>, IDisposable> subscribe)
    {
        ArgumentNullException.ThrowIfNull(subscribe);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_subscription is not null)
                throw new InvalidOperationException("SessionChannelBridge can only be attached once.");

            _subscription = subscribe(evt =>
            {
                // Każde poniższe zdarzenie odwzorowuje jeden sygnał cyklu życia SDK na zachowanie
                // strumienia. Zdarzenia tekstowe produkują fragmenty; zdarzenia końcowe zamykają
                // kanał, aby odpowiedź HTTP mogła zakończyć się czysto.
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta when !string.IsNullOrEmpty(delta.Data.DeltaContent):
                        // Sesje Streaming=true emitują delty tokenów/treści w miarę
                        // jak asystent generuje tekst. Przekaż je natychmiast,
                        // aby uzyskać niskie opóźnienia Server-Sent Events.
                        _channel.Writer.TryWrite(delta.Data.DeltaContent);
                        break;

                    case AssistantMessageEvent msg when !string.IsNullOrEmpty(msg.Data.Content):
                        // Sesje Streaming=false mogą emitować tylko ukończone zdarzenie
                        // AssistantMessageEvent, więc pomost traktuje tę pełną wiadomość
                        // jako pojedynczy fragment strumienia.
                        _channel.Writer.TryWrite(msg.Data.Content);
                        break;

                    case SessionIdleEvent:
                        // Bezczynność to zdarzenie SDK kończące daną turę. Zamknięcie
                        // kanału pozwala oczekującym zakończyć w sposób naturalny.
                        _channel.Writer.TryComplete();
                        break;

                    case SessionErrorEvent err:
                        // Błędy SDK/środowiska wykonawczego ujawniaj w czytniku asynchronicznym,
                        // zamiast kończyć strumień tak, jakby się powiódł.
                        _channel.Writer.TryComplete(new InvalidOperationException(err.Data.Message));
                        break;
                }
            });
        }
    }

    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken = default)
        // Konsumenci widzą zwykły strumień asynchroniczny, mimo że źródłem jest
        // oparte na wywołaniach zwrotnych API CopilotSession.On.
        => _channel.Reader.ReadAllAsync(cancellationToken);

    public void Dispose()
    {
        IDisposable? subscription;
        lock (_gate)
        {
            if (_disposed) return;

            _disposed = true;
            subscription = _subscription;
            _subscription = null;
        }

        subscription?.Dispose();
        _channel.Writer.TryComplete();
    }
}
