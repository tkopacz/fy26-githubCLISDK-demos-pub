using CopilotSDK.Demos.Shared.Infrastructure;
using FluentAssertions;
using GitHub.Copilot;

namespace CopilotSDK.Demos.Tests.Infrastructure;

public class SessionChannelBridgeTests
{
    // ── Pomocnicy fabryczni dla obiektów zdarzeń (SDK ma wymagane składowe) ──────
    // Te testy konstruują rzeczywiste DTO zdarzeń SDK i wprowadzają je do AttachCore,
    // dzięki czemu zachowanie pomostu jest sprawdzane bez uruchamiania środowiska wykonawczego Copilot.

    private static AssistantMessageDeltaEvent Delta(string content) => new()
    {
        Data = new AssistantMessageDeltaData { DeltaContent = content, MessageId = "msg-1" }
    };

    private static AssistantMessageEvent Message(string content) => new()
    {
        Data = new AssistantMessageData { Content = content, MessageId = "msg-1" }
    };

    private static SessionIdleEvent Idle() => new()
    {
        Data = new SessionIdleData()
    };

    private static SessionErrorEvent Error(string msg) => new()
    {
        Data = new SessionErrorData { Message = msg, ErrorType = "test_error" }
    };

    // ── Pomocnik: tworzy pomost z testowalnym AttachCore (InternalsVisibleTo) ───
    // AttachCore reprezentuje szew subskrypcji session.On. Przechwycone
    // wywołanie zwrotne zastępuje środowisko wykonawcze SDK emitujące obiekty SessionEvent.

    private static (SessionChannelBridge bridge, Action<SessionEvent> emit) CreateBridge()
    {
        Action<SessionEvent>? captured = null;
        var bridge = new SessionChannelBridge();
        bridge.AttachCore(handler =>
        {
            captured = handler;
            return new CallbackDisposable(() => captured = null);
        });
        return (bridge, evt => captured?.Invoke(evt));
    }

    // ── Testy ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadAllAsync_YieldsDeltaTokens_WhenDeltaEventsEmitted()
    {
        var (bridge, emit) = CreateBridge();

        // AssistantMessageDeltaEvent to ścieżka strumieniowa SDK. Każda emitowana
        // delta powinna stać się jednym elementem strumienia asynchronicznego aż do SessionIdleEvent.
        emit(Delta("Hello"));
        emit(Delta(" World"));
        emit(Delta("!"));
        emit(Idle());

        var tokens = await CollectAsync(bridge.ReadAllAsync());

        tokens.Should().Equal("Hello", " World", "!");
    }

    [Fact]
    public async Task ReadAllAsync_YieldsFullContent_WhenNonStreamingMessageEvent()
    {
        var (bridge, emit) = CreateBridge();

        // Sesje niestrumieniowe mogą emitować tylko końcową treść AssistantMessageEvent;
        // pomost nadal udostępnia ją w ramach tego samego kontraktu strumieniowego.
        emit(Message("Full response"));
        emit(Idle());

        var tokens = await CollectAsync(bridge.ReadAllAsync());

        tokens.Should().ContainSingle().Which.Should().Be("Full response");
    }

    [Fact]
    public async Task ReadAllAsync_IgnoresEmptyDeltaTokens()
    {
        var (bridge, emit) = CreateBridge();

        emit(Delta(""));   // pusty — powinien być zignorowany
        emit(Delta("text"));
        emit(Idle());

        var tokens = await CollectAsync(bridge.ReadAllAsync());

        tokens.Should().ContainSingle().Which.Should().Be("text");
    }

    [Fact]
    public async Task ReadAllAsync_ThrowsOnSessionError()
    {
        var (bridge, emit) = CreateBridge();

        // SessionErrorEvent powinno powodować błąd strumienia asynchronicznego, aby wywołujący
        // ASP.NET zobaczyli nieudaną turę SDK zamiast udanej pustej odpowiedzi.
        emit(Error("Connection lost"));

        var act = async () => await CollectAsync(bridge.ReadAllAsync());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Connection lost*");
    }

    [Fact]
    public async Task ReadAllAsync_CompletesImmediately_AfterDispose()
    {
        var (bridge, _) = CreateBridge();
        bridge.Dispose();

        var tokens = await CollectAsync(bridge.ReadAllAsync());

        tokens.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_DoesNotThrow_WhenCalledMultipleTimes()
    {
        var (bridge, _) = CreateBridge();

        var act = () =>
        {
            bridge.Dispose();
            bridge.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void AttachCore_Throws_WhenAlreadyAttached()
    {
        var (bridge, _) = CreateBridge();

        var act = () => bridge.AttachCore(_ => new CallbackDisposable(() => { }));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*only be attached once*");
    }

    [Fact]
    public void AttachCore_Throws_WhenDisposed()
    {
        var bridge = new SessionChannelBridge();
        bridge.Dispose();

        var act = () => bridge.AttachCore(_ => new CallbackDisposable(() => { }));

        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_UnsubscribesCapturedCallback()
    {
        Action<SessionEvent>? captured = null;
        var disposed = false;
        using var bridge = new SessionChannelBridge();
        bridge.AttachCore(handler =>
        {
            captured = handler;
            return new CallbackDisposable(() =>
            {
                disposed = true;
                captured = null;
            });
        });

        bridge.Dispose();

        disposed.Should().BeTrue();
        captured.Should().BeNull();
    }

    [Fact]
    public async Task ReadAllAsync_EmitsMultipleTypes_InOrder()
    {
        var (bridge, emit) = CreateBridge();

        emit(Delta("chunk1"));
        emit(Delta("chunk2"));
        emit(Idle());

        var tokens = await CollectAsync(bridge.ReadAllAsync());

        tokens.Should().HaveCount(2).And.ContainInOrder("chunk1", "chunk2");
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> source)
    {
        var result = new List<string>();
        await foreach (var item in source)
            result.Add(item);
        return result;
    }

    private sealed class CallbackDisposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
