using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;

namespace Quacktion.Data.Repositories;

public sealed class MeetingAudioSettings
{
    public string RootPath { get; set; } = "/tmp/quacktion-meetings";
}

// PCM is spooled per device segment. Only a small copy buffer is used to assemble the final WAV.
public sealed class MeetingAudioStore(IOptions<MeetingAudioSettings> settings) : IMeetingAudioStore
{
    private const int CopyBufferSize = 64 * 1024;
    private string DirectoryFor(string id) => Path.Combine(settings.Value.RootPath,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant());
    private string SegmentPath(MeetingSegment segment) => Path.Combine(DirectoryFor(segment.MeetingId), "segments", segment.Id + ".pcm");
    private string FinalPath(string id) => Path.Combine(DirectoryFor(id), "recording.wav");

    public async Task SaveSegmentAsync(MeetingSegment segment, ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        if (segment.Id.Length != 64 || !segment.Id.All(Uri.IsHexDigit) ||
            Convert.ToHexString(SHA256.HashData(pcm.Span)) != segment.AudioHash)
            throw new AppException(409, "invalid_meeting_audio");
        var path = SegmentPath(segment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(DirectoryFor(segment.MeetingId), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                CopyBufferSize, FileOptions.Asynchronous))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await file.WriteAsync(pcm, ct);
                await file.FlushAsync(ct);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<(string Path, long Bytes)> FinalizeAsync(string meetingId, IMeetingSegmentRepo segments, CancellationToken ct)
    {
        var final = FinalPath(meetingId);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(DirectoryFor(meetingId), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temp = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long total = 0;
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                CopyBufferSize, FileOptions.Asynchronous))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await output.WriteAsync(new byte[44], ct);
                for (var offset = 0; ; offset += 100)
                {
                    var page = await segments.OrderedAsync(meetingId, offset, 100, ct);
                    foreach (var segment in page)
                    {
                        var path = SegmentPath(segment);
                        if (!File.Exists(path)) throw new AppException(409, "meeting_audio_missing");
                        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        if (input.Length != segment.Bytes) throw new AppException(409, "meeting_audio_mismatch");
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[CopyBufferSize];
                        int read;
                        while ((read = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), ct);
                            total += read;
                        }
                        if (Convert.ToHexString(hash.GetHashAndReset()) != segment.AudioHash)
                            throw new AppException(409, "meeting_audio_mismatch");
                    }
                    if (page.Count < 100) break;
                }
                if (total > uint.MaxValue - 44) throw new AppException(413, "meeting_audio_too_large");
                output.Position = 0;
                var header = new byte[44];
                Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(total + 36));
                Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), 16000);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 32000);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 2);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
                Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)total);
                await output.WriteAsync(header, ct);
                await output.FlushAsync(ct);
            }
            File.Move(temp, final, true);
            return (final, total + 44);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public Task<Stream?> OpenFinalAsync(string meetingId, CancellationToken ct)
    {
        var path = FinalPath(meetingId);
        Stream? stream = File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan) : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string meetingId, CancellationToken ct)
    {
        var path = DirectoryFor(meetingId);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        return Task.CompletedTask;
    }
}
