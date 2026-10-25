using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;

namespace Quacktion.Data.Agents;

// The file transcription API is separate from the low-latency Gemini Live session.
public sealed class Gemini35MeetingTranscriber(IHttpClientFactory clients, IOptions<GeminiSettings> settings)
    : IFinalMeetingTranscriber
{
    private const int PcmBytesPerSecond = 32000;
    private const int ChunkSeconds = 20 * 60;
    private const int ChunkBytes = PcmBytesPerSecond * ChunkSeconds;

    public async Task<IReadOnlyList<MeetingTranscriptTurn>> TranscribeAsync(Meeting meeting, string wavPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.Value.ApiKey)) throw new AppException(503, "gemini_api_key_not_configured");
        var result = new List<MeetingTranscriptTurn>();
        await using var source = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length <= 44) return result;
        source.Position = 44;
        for (var chunk = 0; source.Position < source.Length; chunk++)
        {
            var pcmBytes = (int)Math.Min(ChunkBytes, source.Length - source.Position);
            var path = Path.Combine(Path.GetTempPath(), "quacktion-transcribe-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                await WriteChunkAsync(source, path, pcmBytes, ct);
                var response = await TranscribeFileAsync(path, ct);
                result.AddRange(ParseTurns(response, meeting, chunk, (long)chunk * ChunkSeconds * 1000));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        return result;
    }

    private static async Task WriteChunkAsync(Stream source, string path, int pcmBytes, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var header = new byte[44];
        file.Write(header);
        var remaining = pcmBytes;
        var buffer = new byte[65536];
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), ct);
            if (read == 0) throw new EndOfStreamException();
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
        file.Position = 0;
        // Copy the header from a known 16 kHz mono 16-bit PCM layout.
        System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(pcmBytes + 36));
        System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 16000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 32000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        System.Text.Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)pcmBytes);
        await file.WriteAsync(header, ct);
    }

    private async Task<JsonDocument> TranscribeFileAsync(string path, CancellationToken ct)
    {
        var client = clients.CreateClient("GeminiMeeting");
        string? uploadedName = null;
        try
        {
            using var start = new HttpRequestMessage(HttpMethod.Post, "upload/v1beta/files");
            start.Headers.Add("x-goog-api-key", settings.Value.ApiKey);
            start.Headers.Add("X-Goog-Upload-Protocol", "resumable");
            start.Headers.Add("X-Goog-Upload-Command", "start");
            start.Headers.Add("X-Goog-Upload-Header-Content-Length", new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture));
            start.Headers.Add("X-Goog-Upload-Header-Content-Type", "audio/wav");
            start.Content = JsonContent.Create(new { file = new { display_name = "meeting-audio" } });
            using var started = await client.SendAsync(start, ct);
            if (!started.IsSuccessStatusCode || !started.Headers.TryGetValues("X-Goog-Upload-URL", out var urls))
                throw new AppException(502, "meeting_audio_upload_failed");
            var uploadUrl = urls.First();
            if (!Uri.TryCreate(uploadUrl, UriKind.Absolute, out var uploadUri) ||
                uploadUri.Scheme != Uri.UriSchemeHttps || uploadUri.Host != "generativelanguage.googleapis.com")
                throw new AppException(502, "meeting_audio_upload_invalid_url");
            using var upload = new HttpRequestMessage(HttpMethod.Post, uploadUri);
            upload.Headers.Add("x-goog-api-key", settings.Value.ApiKey);
            upload.Headers.Add("X-Goog-Upload-Offset", "0");
            upload.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
            upload.Content = new StreamContent(File.OpenRead(path));
            upload.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            using var uploaded = await client.SendAsync(upload, ct);
            if (!uploaded.IsSuccessStatusCode) throw new AppException(502, "meeting_audio_upload_failed");
            using var fileResponse = await JsonDocument.ParseAsync(await uploaded.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var file = fileResponse.RootElement.GetProperty("file");
            var uri = file.GetProperty("uri").GetString();
            uploadedName = file.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(uri)) throw new AppException(502, "meeting_audio_upload_failed");
            using var request = new HttpRequestMessage(HttpMethod.Post, "v1beta/interactions");
            request.Headers.Add("x-goog-api-key", settings.Value.ApiKey);
            request.Content = JsonContent.Create(new
            {
                model = "gemini-3.5-transcribe",
                input = new[] { new { type = "audio", uri, mime_type = "audio/wav" } },
                generation_config = new { transcription_config = new { mode = new
                { type = "verbatim", diarization_mode = "speaker", timestamp_granularities = new[] { "word" } } } }
            });
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new AppException(502, "meeting_transcription_provider_failed");
            var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (document.RootElement.TryGetProperty("status", out var status) && status.GetString() != "completed")
            { document.Dispose(); throw new AppException(502, "meeting_transcription_incomplete"); }
            return document;
        }
        finally
        {
            if (uploadedName is not null && uploadedName.StartsWith("files/", StringComparison.Ordinal))
            {
                try
                {
                    using var delete = new HttpRequestMessage(HttpMethod.Delete, "v1beta/" + uploadedName);
                    delete.Headers.Add("x-goog-api-key", settings.Value.ApiKey);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var _ = await client.SendAsync(delete, cleanup.Token);
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
                { /* Files API retention remains Google's fallback. */ }
            }
        }
    }

    private static IReadOnlyList<MeetingTranscriptTurn> ParseTurns(JsonDocument document, Meeting meeting, int chunk, long baseMs)
    {
        using (document)
        {
            var turns = new List<MeetingTranscriptTurn>();
            if (!document.RootElement.TryGetProperty("steps", out var steps))
                throw new AppException(502, "meeting_transcription_missing_words");
            var words = steps.EnumerateArray().Where(s => s.TryGetProperty("content", out _))
                .SelectMany(s => s.GetProperty("content").EnumerateArray())
                .Where(c => c.TryGetProperty("annotations", out _))
                .SelectMany(c => c.GetProperty("annotations").EnumerateArray())
                .Where(a => a.TryGetProperty("type", out var type) && type.GetString() == "word_info");
            MeetingTranscriptTurn? current = null;
            foreach (var word in words)
            {
                var text = word.TryGetProperty("text", out var value) ? value.GetString() : null;
                if (string.IsNullOrWhiteSpace(text)) continue;
                var rawSpeaker = word.TryGetProperty("speaker", out var speaker) ? speaker.GetString() : null;
                // Chunk-local labels are deliberately not presented as persistent identities.
                var speakerId = string.IsNullOrWhiteSpace(rawSpeaker) ? null : "chunk_" + chunk + "_" + rawSpeaker;
                var start = Offset(word, "start_offset", baseMs);
                var end = Offset(word, "end_offset", baseMs);
                if (current is null || current.SpeakerId != speakerId ||
                    start is { } next && current.EndMs is { } previous && next - previous > 1500 ||
                    current.Text.Length > 1000)
                {
                    current = new MeetingTranscriptTurn { MeetingId = meeting.Id, OwnerAccountId = meeting.OwnerAccountId,
                        ChunkIndex = chunk, SpeakerId = speakerId, StartMs = start };
                    current.Id = Secrets.Digest(meeting.Id, "turn:" + chunk.ToString(CultureInfo.InvariantCulture) + ":" + turns.Count.ToString(CultureInfo.InvariantCulture));
                    turns.Add(current);
                }
                current.Text += current.Text.Length == 0 || char.IsPunctuation(text[0]) ? text : " " + text;
                current.EndMs = end ?? current.EndMs;
            }
            if (turns.Count == 0 && document.RootElement.TryGetProperty("output_text", out var output)
                && !string.IsNullOrWhiteSpace(output.GetString()))
                throw new AppException(502, "meeting_transcription_missing_words");
            return turns;
        }
    }

    private static long? Offset(JsonElement word, string key, long baseMs)
    {
        if (!word.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var raw = value.GetString();
        return raw is not null && raw.EndsWith('s') && double.TryParse(raw[..^1], NumberStyles.Float,
            CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? baseMs + (long)Math.Round(seconds * 1000) : null;
    }
}
