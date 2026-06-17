using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace CopilotSDK.Demos.Shared.Infrastructure;

/// <summary>
/// Śledzi (tail) dane telemetryczne zapisywane przez środowisko wykonawcze Copilot SDK do pliku JSONL.
///
/// Gdy <see cref="CopilotClientFactory"/> włącza TelemetryConfig z ExporterType="file",
/// proces środowiska wykonawczego zapisuje rekordy zakresów (spans) na dysk. Tych zakresów
/// nie da się pobrać z ActivitySource tego procesu, ponieważ środowisko wykonawcze Copilot
/// to osobny proces. Ten obserwator konwertuje plik na kanał, dzięki czemu wersje
/// demonstracyjne mogą pokazywać w czasie rzeczywistym niskopoziomową aktywność środowiska wykonawczego/RPC.
/// </summary>
public sealed class TelemetryObserver : IDisposable, IAsyncDisposable
{
    public record RpcEntry(
        string Name,
        DateTimeOffset StartTime,
        TimeSpan? Duration,
        IReadOnlyList<KeyValuePair<string, string?>> Tags);

    private readonly Channel<RpcEntry> _channel = Channel.CreateUnbounded<RpcEntry>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly string _filePath;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _tailTask;

    private long _position;
    private string _incomplete = string.Empty;
    private bool _disposed;

    public TelemetryObserver(string filePath)
    {
        // Rozpocznij od bieżącego końca pliku, aby demo pokazywało dane telemetryczne
        // emitowane dla nowego uruchomienia SDK, a nie nieaktualne zakresy z wcześniejszego procesu.
        _filePath = filePath;
        _position = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
        _tailTask = TailFileAsync(_cts.Token);
    }

    private async Task TailFileAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                await ReadNewLinesAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Zażądano zatrzymania; ostateczne wypłukanie następuje poniżej.
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (failure is null)
            {
                try
                {
                    // Daj środowisku wykonawczemu Copilot chwilę na wypłukanie pozostałych
                    // zakresów po zaobserwowaniu SessionIdleEvent/Stop.
                    await Task.Delay(200).ConfigureAwait(false);
                    await ReadNewLinesAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }

            _channel.Writer.TryComplete(failure);
        }
    }

    private async Task ReadNewLinesAsync()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            // Środowisko wykonawcze SDK może nadal zapisywać ten plik, gdy obserwator
            // czyta, więc udostępnij dostęp do odczytu/zapisu/usuwania i zachowaj naszą
            // pozycję bajtową między odpytaniami.
            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= _position) return;

            fs.Seek(_position, SeekOrigin.Begin);
            var toRead = (int)(fs.Length - _position);
            var buffer = new byte[toRead];
            var read = await fs.ReadAsync(buffer.AsMemory(0, toRead)).ConfigureAwait(false);
            _position += read;

            var chunk = _incomplete + Encoding.UTF8.GetString(buffer, 0, read);
            var lines = chunk.Split('\n');
            _incomplete = chunk.EndsWith('\n') ? string.Empty : lines[^1];
            var count = chunk.EndsWith('\n') ? lines.Length : lines.Length - 1;

            for (var i = 0; i < count; i++)
            {
                var line = lines[i].Trim('\r');
                if (string.IsNullOrEmpty(line)) continue;
                // Tylko rekordy zakresów (spans) stają się wartościami RpcEntry. Inne typy
                // rekordów telemetrycznych są celowo ignorowane przez ParseSpan.
                var entry = ParseSpan(line);
                if (entry is not null)
                    _channel.Writer.TryWrite(entry);
            }
        }
        catch (IOException)
        {
            // Chwilowo zablokowany — spróbuj ponownie przy następnym odpytaniu.
        }
    }

    private static DateTimeOffset ParseOtlpTime(JsonElement el)
    {
        var arr = el.EnumerateArray().ToArray();
        if (arr.Length != 2) return default;
        var seconds = arr[0].GetInt64();
        var nanos = arr[1].GetInt64();
        return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(nanos / 100);
    }

    private static RpcEntry? ParseSpan(string json)
    {
        try
        {
            // Eksporter plików zapisuje jeden obiekt JSON w każdym wierszu. Tylko wersja
            // demonstracyjna 03 musi pokazać nazwę operacji, znaczniki czasu, czas trwania
            // i atrybuty tego, co środowisko wykonawcze SDK zrobiło pod maską.
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "span")
                return null;

            var name = root.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString() ?? string.Empty
                : string.Empty;

            var startTime = root.TryGetProperty("startTime", out var startEl)
                ? ParseOtlpTime(startEl)
                : default;

            TimeSpan? duration = null;
            if (root.TryGetProperty("endTime", out var endEl))
                duration = ParseOtlpTime(endEl) - startTime;

            var tags = new List<KeyValuePair<string, string?>>();
            if (root.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Object)
            {
                foreach (var attr in attrs.EnumerateObject())
                {
                    var val = attr.Value.ValueKind switch
                    {
                        JsonValueKind.String => attr.Value.GetString(),
                        JsonValueKind.Number => attr.Value.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => attr.Value.GetRawText(),
                    };
                    tags.Add(new(attr.Name, val));
                }
            }

            return new RpcEntry(name, startTime, duration, tags);
        }
        catch (JsonException)
        {
            // Częściowo zapisana linia lub linia niezawierająca zakresu nie powinna powodować
            // awarii interfejsu demonstracyjnego; następne odpytanie przetworzy kompletne linie.
            return null;
        }
    }

    public ChannelReader<RpcEntry> Entries => _channel.Reader;

    public void Stop()
    {
        if (!_cts.IsCancellationRequested)
            _cts.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        try
        {
            _tailTask.GetAwaiter().GetResult();
        }
        finally
        {
            _cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        try
        {
            await _tailTask.ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
