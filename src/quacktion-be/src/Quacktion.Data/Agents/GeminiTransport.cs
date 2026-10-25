using System.Net.WebSockets;
using System.Text.Json;
using Quacktion.App.Services;

namespace Quacktion.Data.Agents;

public interface IGeminiSocketFactory
{
    Task<WebSocket> ConnectAsync(string apiKey, CancellationToken ct);
}

public sealed class GeminiSocketFactory : IGeminiSocketFactory
{
    public async Task<WebSocket> ConnectAsync(string apiKey, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        try { await socket.ConnectAsync(GeminiProtocol.AuthenticatedUri(apiKey), ct); return socket; }
        catch { socket.Dispose(); throw; }
    }
}

public static class GeminiTransport
{
    public static Task SendAsync(WebSocket socket, object message, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, GeminiProtocol.Json), WebSocketMessageType.Text, true, ct);

    public static async Task<ServerMessage> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new AppException(502, CloseCode(result.CloseStatus, result.CloseStatusDescription));
            if (bytes.Length + result.Count > 1_048_576) throw new AppException(502, "gemini_message_too_large");
            bytes.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        // Gemini may deliver JSON in binary frames; both contain UTF-8 JSON.
        return JsonSerializer.Deserialize<ServerMessage>(bytes.ToArray(), GeminiProtocol.Json)
            ?? throw new AppException(502, "gemini_invalid_message");
    }

    public static string CloseCode(WebSocketCloseStatus? status, string? description)
    {
        // Classify without logging arbitrary provider descriptions, which can echo private data.
        var reason = description?.ToLowerInvariant() ?? "";
        if (reason.Contains("api key") || reason.Contains("permission") || reason.Contains("unauthenticated")) return "gemini_authentication_failed";
        if (reason.Contains("quota") || reason.Contains("rate limit") || reason.Contains("resource exhausted")) return "gemini_rate_limited";
        if (reason.Contains("not found") || reason.Contains("not supported for")) return "gemini_model_unavailable";
        if (status == WebSocketCloseStatus.InvalidPayloadData) return "gemini_invalid_setup";
        if (status == WebSocketCloseStatus.NormalClosure) return "gemini_session_ended";
        return "gemini_connection_closed";
    }

    public static string SetupDiagnostic(string? description)
    {
        // Return only fixed labels. Provider errors can echo instructions, handles or credentials.
        var reason = description?.ToLowerInvariant() ?? "";
        if (reason.Contains("unknown name") || reason.Contains("unknown field")) return "unknown_field";
        if (reason.Contains("resumption") || reason.Contains("session_resumption")) return "session_resumption";
        if (reason.Contains("voice")) return "voice_configuration";
        if (reason.Contains("activity") || reason.Contains("sensitivity") || reason.Contains("silence")) return "activity_detection";
        if (reason.Contains("tool") || reason.Contains("function") || reason.Contains("non_blocking")) return "tool_configuration";
        if (reason.Contains("model")) return "model_configuration";
        if (reason.Contains("invalid argument")) return "invalid_argument";
        return "unclassified";
    }
}
