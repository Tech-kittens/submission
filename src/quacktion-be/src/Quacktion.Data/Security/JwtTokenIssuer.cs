using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
namespace Quacktion.Data.Security;

public sealed class JwtSettings
{
    public string Issuer { get; set; } = "Quacktion";
    public string Audience { get; set; } = "Quacktion.Frontend";
    public string SigningKey { get; set; } = "";
}
public sealed class JwtTokenIssuer(IOptions<JwtSettings> settings) : ITokenIssuer
{
    public (string Token, DateTime ExpiresAt) Issue(Account account)
    {
        var now = DateTime.UtcNow; var expires = now.AddMinutes(10);
        var jwt = new JwtSecurityToken(settings.Value.Issuer, settings.Value.Audience,
            [new Claim(JwtRegisteredClaimNames.Sub, account.Id), new Claim("stamp", account.SecurityStamp), new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))], now, expires,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Value.SigningKey)), SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}
