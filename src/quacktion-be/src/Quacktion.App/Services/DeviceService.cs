using Microsoft.Extensions.Logging;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
namespace Quacktion.App.Services;

public sealed class DeviceService(IDeviceRepo devices, IPairingChallengeRepo challenges, IAccountService accounts,
    IDeviceConnectionManager connections, ILogger<DeviceService> logger) : IDeviceService
{
    public async Task<object> CreateAsync(string owner, DeviceRequest request, CancellationToken ct)
    {
        await accounts.GetAsync(owner, ct);
        var device = new Device { OwnerAccountId = owner, DeviceIdentifier = request.DeviceIdentifier, Name = request.Name, FirmwareVersion = request.FirmwareVersion };
        await devices.InsertAsync(device, ct);
        return new { Device = device, Pairing = await ChallengeAsync(owner, device.Id, ct) };
    }
    public async Task<Device> GetAsync(string owner, string id, CancellationToken ct) => Guard.Found(await devices.FindAsync(x => x.Id == id && x.OwnerAccountId == owner, ct));
    public async Task<IReadOnlyList<Device>> ListAsync(string owner, int offset, int limit, CancellationToken ct)
    { Guard.Page(offset, limit); return await devices.ListAsync(x => x.OwnerAccountId == owner, offset, limit, ct); }
    public async Task<Device> UpdateAsync(string owner, string id, DeviceUpdate request, CancellationToken ct)
    {
        var device = await GetAsync(owner, id, ct); device.Name = request.Name; device.FirmwareVersion = request.FirmwareVersion;
        await Guard.Save(devices, device, ct); return device;
    }
    public async Task<object> ChallengeAsync(string owner, string id, CancellationToken ct)
    {
        var device = await GetAsync(owner, id, ct); Guard.State(device.CredentialHash is null, "already_paired");
        var secret = Secrets.New(); var challenge = new PairingChallenge { DeviceId = id, AccountId = owner, Hash = Secrets.Hash(secret), ExpiresAt = DateTime.UtcNow.AddMinutes(5) };
        await challenges.InsertAsync(challenge, ct);
        return new { ChallengeId = challenge.Id, Challenge = secret, challenge.ExpiresAt };
    }
    public async Task<object> PairAsync(string owner, string id, PairRequest request, CancellationToken ct)
    {
        await accounts.GetAsync(owner, ct);
        var device = await GetAsync(owner, id, ct); Guard.State(device.CredentialHash is null, "already_paired");
        var challenge = Guard.Found(await challenges.GetAsync(request.ChallengeId, ct));
        if (challenge.AccountId != owner || challenge.DeviceId != id || challenge.Consumed || challenge.ExpiresAt <= DateTime.UtcNow || !Secrets.Matches(request.Challenge, challenge.Hash))
            throw new AppException(400, "invalid_pairing_challenge");
        challenge.Consumed = true; await Guard.Save(challenges, challenge, ct);
        var credential = Secrets.New(); device.CredentialHash = Secrets.Hash(credential); device.Status = DeviceStatus.Offline;
        await Guard.Save(devices, device, ct);
        logger.LogInformation("DevicePaired {AccountId} {DeviceId}", owner, id);
        return new { DeviceId = id, Credential = credential };
    }
    public async Task UnpairAsync(string owner, string id, bool delete, CancellationToken ct)
    {
        var device = await GetAsync(owner, id, ct);
        Guard.State(device.ActiveSessionId is null, "device_has_active_session");
        device.CredentialHash = null; device.Status = DeviceStatus.Unpaired;
        await Guard.Save(devices, device, ct); await connections.DisconnectAsync(id, ct);
        if (delete) Guard.State(await devices.DeleteAsync(device, ct));
        logger.LogInformation("DeviceUnpaired {AccountId} {DeviceId}", owner, id);
    }
}
public sealed class DeviceAuthenticationService(IDeviceRepo devices, IAccountRepo accounts) : IDeviceAuthenticationService
{
    public async Task<Device?> AuthenticateAsync(string id, string credential, CancellationToken ct)
    {
        var device = await AuthenticateCredentialAsync(credential, ct);
        return device?.Id == id ? device : null;
    }

    public async Task<Device?> AuthenticateCredentialAsync(string credential, CancellationToken ct)
    {
        if (credential.Length != 64) return null;
        var hash = Secrets.Hash(credential);
        var device = await devices.FindAsync(d => d.CredentialHash == hash, ct);
        if (device is null || !Secrets.Matches(credential, device.CredentialHash)) return null;
        var account = await accounts.GetAsync(device.OwnerAccountId, ct);
        return account is { IsActive: true } ? device : null;
    }
}
