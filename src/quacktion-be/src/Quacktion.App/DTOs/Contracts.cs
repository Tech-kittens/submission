using System.ComponentModel.DataAnnotations;
using Quacktion.App.Models;

namespace Quacktion.App.DTOs;

public record RegisterRequest(
    [Required, EmailAddress, MaxLength(254)] string Email,
    [Required, MinLength(12), MaxLength(128)] string Password,
    [Required, MaxLength(128)] string Name
);

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required, MaxLength(128)] string Password
);

public record RefreshRequest([Required, MaxLength(512)] string RefreshToken);

public record ProfileRequest(
    [Required, MaxLength(128)] string Name
);

public record PasswordRequest(
    [Required] string CurrentPassword,
    [Required, MinLength(12), MaxLength(128)] string NewPassword
);

public record TokenResult(string AccessToken, string RefreshToken, DateTime ExpiresAt);

public record DeviceRequest(
    [Required, MaxLength(128)] string DeviceIdentifier,
    [Required, MaxLength(128)] string Name,
    [MaxLength(64)] string FirmwareVersion = ""
);

public record DeviceUpdate(
    [Required, MaxLength(128)] string Name,
    [MaxLength(64)] string FirmwareVersion
);

public record PairRequest(
    [Required] string ChallengeId,
    [Required, MaxLength(128)] string Challenge
);

public record SessionRequest(
    [MaxLength(200)] string? Name,
    string? DeviceId,
    [MaxLength(8192)] string? InitialPrompt = null,
    string PromptFormat = "text",
    [MaxLength(200)] string? Title = null
)
{
    public string EffectiveName => Name ?? Title ?? "";
}

public record SessionStartRequest(
    [MaxLength(8192)] string? InitialPrompt,
    string PromptFormat = "text",
    [MaxLength(200)] string? RequestId = null
);

public record SessionAudioStartRequest(
    [MaxLength(200)] string? RequestId = null,
    [MaxLength(200)] string Name = "Audio session"
);

public record SessionAudioStartResult(Session Session, int ExpiresInSeconds);

public record SessionUpdate([Required, MaxLength(200)] string Title);

public record ProposalRequest(
    [Required, MaxLength(128)] string ActionType,
    [Required, MaxLength(32768)] string ParametersJson
);

public record ApprovalDecision([Required] string ActionDigest);

public record Page<T>(IReadOnlyList<T> Items, int Offset, int Limit);
