using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public enum QuacktionStatus { Proposed, PendingApproval, Queued, InProgress, Completed, Failed, Cancelled, Rejected }
