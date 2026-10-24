using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
namespace Quacktion.App.Services;

public sealed class AccountService(IAccountRepo accounts, IRefreshTokenRepo tokens, IPasswordHasher<Account> hasher,
    ITokenIssuer issuer, ILogger<AccountService> logger) : IAccountService
{
    public async Task<Account> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        Guard.Input(request.Password.Length is >= 12 and <= 128);
        var account = new Account { Email = request.Email.Trim().ToUpperInvariant(), Name = request.Name };
        account.PasswordHash = hasher.HashPassword(account, request.Password);
        await accounts.InsertAsync(account, ct);
        logger.LogInformation("AccountCreated {AccountId}", account.Id);
        return account;
    }
    public async Task<Account> GetAsync(string owner, CancellationToken ct) => Guard.Found(await accounts.FindAsync(x => x.Id == owner && x.IsActive, ct));
    public async Task<Account> UpdateAsync(string owner, ProfileRequest request, CancellationToken ct)
    {
        var account = await GetAsync(owner, ct);
        account.Name = request.Name; account.UpdatedAt = DateTime.UtcNow;
        await Guard.Save(accounts, account, ct); return account;
    }
    public async Task DeleteAsync(string owner, CancellationToken ct)
    {
        var account = await GetAsync(owner, ct);
        account.IsActive = false; account.SecurityStamp = Secrets.New(); account.PasswordHash = ""; account.UpdatedAt = DateTime.UtcNow;
        await Guard.Save(accounts, account, ct);
        // All user/device/worker authorization checks consult IsActive. Cleanup is restart-safe background work.
        logger.LogInformation("AccountDeactivated {AccountId}", owner);
    }
    public async Task ChangePasswordAsync(string owner, PasswordRequest request, CancellationToken ct)
    {
        var account = await GetAsync(owner, ct);
        if (hasher.VerifyHashedPassword(account, account.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
            throw new AppException(401, "invalid_credentials");
        Guard.Input(request.NewPassword.Length is >= 12 and <= 128);
        account.PasswordHash = hasher.HashPassword(account, request.NewPassword);
        account.SecurityStamp = Secrets.New(); account.UpdatedAt = DateTime.UtcNow;
        await Guard.Save(accounts, account, ct);
        logger.LogInformation("PasswordChanged {AccountId}", owner);
    }
    public async Task<TokenResult> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToUpperInvariant();
        var account = await accounts.FindAsync(x => x.Email == email && x.IsActive, ct);
        // Dummy hash verification also performs the PBKDF work for unknown accounts.
        var candidate = account ?? new Account();
        var hash = account?.PasswordHash ?? DummyHash;
        var verified = hasher.VerifyHashedPassword(candidate, hash, request.Password);
        if (account is null || verified == PasswordVerificationResult.Failed) throw new AppException(401, "invalid_credentials");
        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
        { account.PasswordHash = hasher.HashPassword(account, request.Password); await Guard.Save(accounts, account, ct); }
        var secret = Secrets.New();
        var family = new RefreshToken { AccountId = account.Id, SecurityStamp = account.SecurityStamp, CurrentHash = Secrets.Hash(secret), ExpiresAt = DateTime.UtcNow.AddDays(30) };
        await tokens.InsertAsync(family, ct);
        return Issue(account, family.Id + "." + secret);
    }
    private static readonly string DummyHash = new PasswordHasher<Account>().HashPassword(new Account(), Secrets.New());
    public async Task<TokenResult> RefreshAsync(string token, CancellationToken ct)
    {
        var (id, secret) = Split(token);
        var family = await tokens.GetAsync(id, ct);
        if (family is null || family.Revoked || family.ExpiresAt <= DateTime.UtcNow) throw new AppException(401, "invalid_refresh_token");
        if (!Secrets.Matches(secret, family.CurrentHash))
        {
            if (family.UsedHashes.Contains(Secrets.Hash(secret))) await RevokeAsync(id, ct);
            throw new AppException(401, "invalid_refresh_token");
        }
        var account = await accounts.GetAsync(family.AccountId, ct);
        if (account is null || !account.IsActive || account.SecurityStamp != family.SecurityStamp) throw new AppException(401, "invalid_refresh_token");
        // Fixed family lifetime and bounded rotation history; require a fresh login after 500 rotations.
        if (family.UsedHashes.Count >= 500) { await RevokeAsync(id, ct); throw new AppException(401, "login_required"); }
        var next = Secrets.New(); family.UsedHashes.Add(family.CurrentHash); family.CurrentHash = Secrets.Hash(next);
        if (!await tokens.ReplaceAsync(family, ct))
        { await RevokeAsync(id, ct); throw new AppException(401, "refresh_reuse_detected"); }
        return Issue(account, id + "." + next);
    }
    public async Task LogoutAsync(string token, CancellationToken ct)
    {
        var (id, secret) = Split(token); var family = await tokens.GetAsync(id, ct);
        if (family is not null && (Secrets.Matches(secret, family.CurrentHash) || family.UsedHashes.Contains(Secrets.Hash(secret)))) await RevokeAsync(id, ct);
    }
    private async Task RevokeAsync(string id, CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var family = await tokens.GetAsync(id, ct); if (family is null || family.Revoked) return;
            family.Revoked = true; if (await tokens.ReplaceAsync(family, ct)) return;
        }
        throw new AppException(409, "concurrency_conflict");
    }
    private static (string Id, string Secret) Split(string token)
    {
        var parts = token.Split('.'); if (parts.Length != 2) throw new AppException(401, "invalid_refresh_token");
        return (parts[0], parts[1]);
    }
    private TokenResult Issue(Account account, string refresh) { var access = issuer.Issue(account); return new(access.Token, refresh, access.ExpiresAt); }
}
