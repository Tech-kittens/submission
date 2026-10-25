using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Preparation;
namespace Quacktion.Web.Controllers;

[Route("api/notifications")]
public sealed class NotificationsController(DeviceNotificationService notifications) : OwnedController
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await notifications.Notifications.Find(x => x.OwnerAccountId == Owner)
        .SortByDescending(x => x.CreatedAt).Limit(100).ToListAsync(ct));
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct) => Ok(Guard.Found(await notifications.Notifications
        .Find(x => x.Id == id && x.OwnerAccountId == Owner).FirstOrDefaultAsync(ct)));
    [HttpPost("{id}/dismiss")]
    public async Task<IActionResult> Dismiss(string id, CancellationToken ct)
    {
        var result = await notifications.Notifications.UpdateOneAsync(x => x.Id == id && x.OwnerAccountId == Owner,
            Builders<DeviceNotification>.Update.Set(x => x.Status, "Dismissed").Set(x => x.OfferResolution, "Dismissed"), cancellationToken: ct);
        if (result.MatchedCount == 0) return NotFound();
        return NoContent();
    }
}
