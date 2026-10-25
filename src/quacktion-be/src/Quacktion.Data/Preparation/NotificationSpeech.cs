using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Quacktion.App.Interfaces;
using Quacktion.App.Services;
using Quacktion.Data.Agents;
using Quacktion.Data.Audio;
namespace Quacktion.Data.Preparation;

public static class NotificationProtocol
{
    public const string BriefingMessage = "You've got a meeting coming up. Would you like your briefing?";
    public static void Validate(DeviceNotificationRequest request)
    {
        Guard.Input(request.NotifyId.Length is > 0 and <= 48 && request.NotifyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "notification_id_invalid");
        Guard.Input(request.Kind is "quack" or "action_done" or "needs_confirmation" or "failed", "notification_kind_invalid");
        Guard.Input(request.ExpiresInSeconds is >= 1 and <= 120 && request.ExpiresAt > request.DueAt, "notification_expiry_invalid");
        // Only application-authored generic speech is permitted. Never read calendar content into the room.
        Guard.Input(request.SpokenMessage is null || request.SpokenMessage == BriefingMessage
            || request.SpokenMessage == "An action is waiting for approval in the app.", "notification_speech_invalid");
    }
    public static IReadOnlyList<OutboundFrame> Frames(string id, string kind, int expiry, byte[]? pcm)
    {
        Validate(new() { NotifyId = id, Kind = kind, ExpiresInSeconds = expiry });
        Guard.Input(pcm is null || pcm.Length is > 0 and <= 320000 && pcm.Length % 2 == 0, "notification_audio_invalid");
        OutboundFrame Control(string type, object payload, string? requestId = null) => new(
            JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type, requestId, payload }), WebSocketMessageType.Text);
        var frames = new List<OutboundFrame> { Control("notify.play", new { notifyId = id, kind, audio = pcm is not null, expiresInSeconds = expiry }, id) };
        if (pcm is null) return frames;
        frames.Add(Control("notify.audio.start", new { notifyId = id, audio = new { codec = "pcm_16000", sampleRate = 16000, channels = 1 } }));
        for (var offset = 0; offset < pcm.Length; offset += 4096)
            frames.Add(new(pcm.AsSpan(offset, Math.Min(4096, pcm.Length - offset)).ToArray(), WebSocketMessageType.Binary));
        frames.Add(Control("notify.audio.end", new { notifyId = id }));
        return frames;
    }
}

public sealed class NotificationSpeech(IHttpClientFactory clients, IOptions<GeminiSettings> gemini, IOptions<PreparationSettings> settings)
{
    public async Task<byte[]> GenerateAsync(string text, CancellationToken ct)
    {
        NotificationProtocol.Validate(new() { SpokenMessage = text });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1beta/models/{settings.Value.NotificationTtsModel}:generateContent");
        request.Headers.Add("x-goog-api-key", gemini.Value.ApiKey);
        request.Content = JsonContent.Create(new {
            contents = new[] { new { parts = new[] { new { text = "Say clearly and briefly: " + text } } } },
            generationConfig = new { responseModalities = new[] { "AUDIO" }, speechConfig = new {
                voiceConfig = new { prebuiltVoiceConfig = new { voiceName = settings.Value.NotificationVoice } } } } });
        using var response = await clients.CreateClient("Gemini").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new AppException(503, "notification_tts_unavailable");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var inline = document.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("inlineData");
        var mime = inline.GetProperty("mimeType").GetString() ?? "";
        Guard.State(mime.StartsWith("audio/L16", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("audio/pcm", StringComparison.OrdinalIgnoreCase), "notification_tts_format_invalid");
        Guard.State(mime.Contains("rate=24000", StringComparison.OrdinalIgnoreCase), "notification_tts_rate_invalid");
        var encoded = inline.GetProperty("data").GetString()!;
        Guard.State(encoded.Length <= 640000, "notification_tts_too_long");
        var source = Convert.FromBase64String(encoded);
        Guard.State(source.Length is > 0 and <= 480000 && source.Length % 2 == 0, "notification_tts_audio_invalid");
        return new PcmResampler().Convert(source);
    }
}
