using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
namespace Quacktion.Data.Sessions;

public sealed class HandleCipher
{
    private readonly byte[]? key;
    public HandleCipher(IConfiguration configuration)
    {
        var encoded = configuration["SessionPersistence:HandleEncryptionKey"];
        if (string.IsNullOrWhiteSpace(encoded)) return;
        try { key = Convert.FromBase64String(encoded); }
        catch (FormatException e) { throw new InvalidOperationException("Session handle encryption key must be base64 encoded.", e); }
        if (key.Length != 32) throw new InvalidOperationException("Session handle encryption key must decode to 32 bytes.");
    }
    public string? Encrypt(string? value, string owner, string session)
    {
        if (value is null || key is null) return null;
        using var aes = new AesGcm(key, 16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(value); var encrypted = new byte[data.Length]; var tag = new byte[16];
        aes.Encrypt(nonce, data, encrypted, tag, Encoding.UTF8.GetBytes(owner + "\n" + session));
        return Convert.ToBase64String(nonce.Concat(tag).Concat(encrypted).ToArray());
    }
    public string? Decrypt(string? value, string owner, string session)
    {
        if (value is null || key is null) return null;
        try
        {
            var data = Convert.FromBase64String(value); if (data.Length < 28) return null;
            var clear = new byte[data.Length - 28]; using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), clear, Encoding.UTF8.GetBytes(owner + "\n" + session));
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }
}
