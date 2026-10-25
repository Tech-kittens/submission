using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Quacktion.App.Models;
using Quacktion.Data.Agents;
using Quacktion.Data.Repositories;
using Xunit;

namespace Quacktion.Web.Tests;

public sealed class MeetingRecordingTests
{
    [Fact]
    public async Task SavedSegmentsProduceOrderedWavAndDetectMissingAudio()
    {
        var root = Path.Combine(Path.GetTempPath(), "quacktion-recording-test-" + Guid.NewGuid().ToString("N"));
        var store = new MeetingAudioStore(Options.Create(new MeetingAudioSettings { RootPath = root }));
        var repo = new MeetingSegments();
        var meetingId = Guid.NewGuid().ToString("N");
        try
        {
            foreach (var (sequence, sample) in new[] { (2, (byte)2), (1, (byte)1) })
            {
                var pcm = Enumerable.Repeat(sample, 1280).ToArray();
                var segment = new MeetingSegment { Id = new string((char)('a' + sequence), 64), MeetingId = meetingId,
                    SegmentId = "boot-" + sequence, BootId = "boot", StartUptimeMs = sequence,
                    Bytes = pcm.Length, AudioHash = Convert.ToHexString(SHA256.HashData(pcm)) };
                await repo.InsertAsync(segment, default);
                await store.SaveSegmentAsync(segment, pcm, default);
            }
            var (path, bytes) = await store.FinalizeAsync(meetingId, repo, default);
            var wav = await File.ReadAllBytesAsync(path);
            Assert.Equal(44 + 2560, bytes);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
            Assert.All(wav[44..1324], b => Assert.Equal((byte)1, b));
            Assert.All(wav[1324..], b => Assert.Equal((byte)2, b));
            await store.DeleteAsync(meetingId, default);
            await Assert.ThrowsAsync<Quacktion.App.Services.AppException>(() => store.FinalizeAsync(meetingId, repo, default));
        }
        finally { await store.DeleteAsync(meetingId, default); }
    }

    [Fact]
    public async Task Gemini35FileTranscriptionKeepsProviderSpeakerLabelsAnonymous()
    {
        var wav = Path.Combine(Path.GetTempPath(), "quacktion-transcriber-test-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await File.WriteAllBytesAsync(wav, new byte[44 + 32000]);
            var handler = new TranscribeHandler();
            var service = new Gemini35MeetingTranscriber(new SingleClientFactory(new HttpClient(handler)
                { BaseAddress = new Uri("https://generativelanguage.googleapis.com/") }),
                Options.Create(new GeminiSettings { ApiKey = "test-key" }));
            var turns = await service.TranscribeAsync(new Meeting { Id = "meeting", OwnerAccountId = "owner" }, wav, default);
            Assert.Equal(2, turns.Count);
            Assert.Equal("Hello world", turns[0].Text);
            Assert.Equal("chunk_0_spk_1", turns[0].SpeakerId);
            Assert.Null(turns[0].SpeakerName);
            Assert.Equal(100, turns[0].StartMs);
            Assert.Equal(800, turns[0].EndMs);
            Assert.Equal("chunk_0_spk_2", turns[1].SpeakerId);
            Assert.True(handler.Deleted);
        }
        finally { File.Delete(wav); }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TranscribeHandler : HttpMessageHandler
    {
        public bool Deleted;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/upload/v1beta/files")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Add("X-Goog-Upload-URL", "https://generativelanguage.googleapis.com/upload/session");
                return Task.FromResult(response);
            }
            if (path == "/upload/session") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { file = new { uri = "files/test", name = "files/test" } }) });
            if (path == "/v1beta/interactions") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { status = "completed", steps = new[] { new { content = new[] { new
            { annotations = new object[] {
                new { type = "word_info", text = "Hello", speaker = "spk_1", start_offset = "0.100s", end_offset = "0.400s" },
                new { type = "word_info", text = "world", speaker = "spk_1", start_offset = "0.500s", end_offset = "0.800s" },
                new { type = "word_info", text = "Yes", speaker = "spk_2", start_offset = "0.900s", end_offset = "1.100s" }
            } } } } } }) });
            if (path == "/v1beta/files/test") { Deleted = true; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
            throw new InvalidOperationException(path);
        }
    }
}
