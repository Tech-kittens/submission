using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

if (args.Length is < 4 or > 8)
{
    Console.Error.WriteLine("Usage: Quacktion.AudioSimulator <base-url> <access-token> <input.wav> <output.wav> [input-codec] [output-codec] [output-rate] [output-channels]");
    return 2;
}

var baseUrl = new Uri(args[0].TrimEnd('/') + "/");
var accessToken = args[1];
var inputPath = Path.GetFullPath(args[2]);
var outputPath = Path.GetFullPath(args[3]);
var wav = PcmWave.Read(inputPath);
var inputCodec = args.Length > 4 ? args[4] : $"pcm_{wav.SampleRate}";
var outputCodec = args.Length > 5 ? args[5] : inputCodec;
var outputRate = args.Length > 6 ? int.Parse(args[6]) : wav.SampleRate;
var outputChannels = args.Length > 7 ? short.Parse(args[7]) : wav.Channels;
if (outputRate <= 0 || outputChannels is < 1 or > 2) throw new InvalidDataException("Output sample rate and channel count are invalid.");
if (!inputCodec.StartsWith("pcm", StringComparison.OrdinalIgnoreCase) || !outputCodec.StartsWith("pcm", StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("The WAV simulator writes PCM WAV files; choose PCM input/output formats or use hardware for other codecs.");
var requestId = Guid.NewGuid().ToString("N");

using var http = new HttpClient { BaseAddress = baseUrl };
using var request = new HttpRequestMessage(HttpMethod.Post, "api/sessions/start")
{
    Content = JsonContent.Create(new { requestId, name = "ESP32-S3 audio simulator" })
};
request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
using var response = await http.SendAsync(request);
if (!response.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"Session start failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
    return 1;
}
using var sessionJson = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
var sessionId = sessionJson.RootElement.GetProperty("sessionId").GetString()!;
var webSocketUrl = new Uri(sessionJson.RootElement.GetProperty("webSocketUrl").GetString()!);
Console.WriteLine($"Session {sessionId} is Ready. Connecting audio...");

using var socket = new ClientWebSocket();
socket.Options.SetRequestHeader("Authorization", "Bearer " + accessToken);
socket.Options.AddSubProtocol("quacktion.audio.v1");
using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
await socket.ConnectAsync(webSocketUrl, stop.Token);
var initial = await ReceiveTextAsync(socket, stop.Token);
Console.WriteLine(initial);
var inputFormat = new { codec = inputCodec, sampleRate = wav.SampleRate, channels = wav.Channels };
var outputFormat = new { codec = outputCodec, sampleRate = outputRate, channels = outputChannels };
await SendTextAsync(socket, JsonSerializer.Serialize(new { version = 1, type = "audio.start", audio = new { protocolVersion = 1, input = inputFormat, output = outputFormat } }), stop.Token);
var ready = await ReceiveTextAsync(socket, stop.Token);
if (!ready.Contains("audio.ready", StringComparison.Ordinal)) throw new InvalidOperationException("Backend did not accept the requested audio format: " + ready);

await using var writer = new PcmWaveWriter(outputPath, outputRate, outputChannels);
var lastAudio = DateTime.UtcNow;
var gotAudio = false;
var endedResponse = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var receive = ReceiveAudioAsync(socket, writer, () => { gotAudio = true; lastAudio = DateTime.UtcNow; }, endedResponse, stop.Token);
var chunkBytes = Math.Max(320, wav.SampleRate * wav.Channels * 2 / 10); // at most 100 ms per device frame
for (var offset = 0; offset < wav.Audio.Length; offset += chunkBytes)
{
    var count = Math.Min(chunkBytes, wav.Audio.Length - offset);
    await socket.SendAsync(wav.Audio.AsMemory(offset, count), WebSocketMessageType.Binary, true, stop.Token);
    await Task.Delay(TimeSpan.FromMilliseconds(100d * count / chunkBytes), stop.Token);
}

var outputDeadline = DateTime.UtcNow.AddSeconds(25);
while (DateTime.UtcNow < outputDeadline && (!gotAudio || DateTime.UtcNow - lastAudio < TimeSpan.FromSeconds(3)))
    await Task.Delay(200, stop.Token);

await SendTextAsync(socket, JsonSerializer.Serialize(new { version = 1, type = "session.end", requestId }), stop.Token);
Console.WriteLine(await endedResponse.Task.WaitAsync(stop.Token));
try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "simulator_complete", stop.Token); }
catch (WebSocketException) { }
try { await receive.WaitAsync(TimeSpan.FromSeconds(3)); }
catch (Exception e) when (e is TimeoutException or OperationCanceledException or WebSocketException) { }
Console.WriteLine($"Saved received audio to {outputPath}");
return 0;

static async Task SendTextAsync(ClientWebSocket socket, string text, CancellationToken ct) =>
    await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

static async Task<string> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct)
{
    var buffer = new byte[8192];
    using var message = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await socket.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Backend closed the WebSocket.");
        message.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);
    return Encoding.UTF8.GetString(message.ToArray());
}

static async Task ReceiveAudioAsync(ClientWebSocket socket, PcmWaveWriter writer, Action received, TaskCompletionSource<string> ended, CancellationToken ct)
{
    var buffer = new byte[65536];
    while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
    {
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        if (result.MessageType == WebSocketMessageType.Binary)
        {
            await writer.WriteAsync(message.ToArray(), ct);
            received();
        }
        else
        {
            var control = Encoding.UTF8.GetString(message.ToArray());
            Console.WriteLine(control);
            if (control.Contains("session.ended", StringComparison.Ordinal)) ended.TrySetResult(control);
            if (control.Contains("session.failed", StringComparison.Ordinal)) return;
        }
    }
}

sealed record PcmWave(byte[] Audio, int SampleRate, short Channels)
{
    public static PcmWave Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException("Input must be a RIFF/WAVE file.");
        short format = 0, channels = 0, bits = 0;
        var sampleRate = 0;
        byte[]? audio = null;
        for (var offset = 12; offset + 8 <= bytes.Length;)
        {
            var id = Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            var body = offset + 8;
            if (size < 0 || body + size > bytes.Length) throw new InvalidDataException("Invalid WAV chunk length.");
            if (id == "fmt ")
            {
                format = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body, 2));
                channels = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4, 4));
                bits = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(body + 14, 2));
            }
            else if (id == "data") audio = bytes.AsSpan(body, size).ToArray();
            offset = body + size + (size & 1);
        }
        if (format != 1 || bits != 16 || channels is < 1 or > 2 || sampleRate <= 0 || audio is null)
            throw new InvalidDataException("Only PCM signed 16-bit mono/stereo WAV input is supported; no resampling is performed.");
        return new(audio, sampleRate, channels);
    }
}

sealed class PcmWaveWriter : IAsyncDisposable
{
    private readonly FileStream stream;
    private readonly int sampleRate;
    private readonly short channels;
    private int written;
    public PcmWaveWriter(string path, int sampleRate, short channels)
    {
        this.sampleRate = sampleRate;
        this.channels = channels;
        stream = File.Create(path);
        stream.Write(new byte[44]);
    }
    public async Task WriteAsync(byte[] bytes, CancellationToken ct)
    {
        await stream.WriteAsync(bytes, ct);
        written = checked(written + bytes.Length);
    }
    public async ValueTask DisposeAsync()
    {
        stream.Position = 0;
        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), checked(written + 36));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), written);
        await stream.WriteAsync(header);
        await stream.DisposeAsync();
    }
}
