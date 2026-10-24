using System.Security.Cryptography;
using System.Text;
using Quacktion.App.Models;
using Quacktion.App.Interfaces;
namespace Quacktion.App.Services;

public sealed class AppException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public static class Guard
{
    public static T Found<T>(T? value) where T : class => value ?? throw new AppException(404, "not_found");
    public static void State(bool valid, string code = "invalid_state") { if (!valid) throw new AppException(409, code); }
    public static void Input(bool valid, string code = "invalid_request") { if (!valid) throw new AppException(400, code); }
    public static async Task Save<T>(IRepository<T> repo, T value, CancellationToken ct) where T : Entity => State(await repo.ReplaceAsync(value, ct), "concurrency_conflict");
    public static (int Offset, int Limit) Page(int offset, int limit) { Input(offset >= 0 && limit is > 0 and <= 100); return (offset, limit); }
}
public static class Secrets
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool Matches(string value, string? hash) => hash is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Hash(value)), Encoding.UTF8.GetBytes(hash));
    public static string Digest(string type, string parameters) => Hash(type + "\n" + parameters);
}
