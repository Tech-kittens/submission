using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

// New states are appended to preserve existing MongoDB numeric enum values.
public enum SessionStatus { Created, Starting, Active, Ending, Completed, Failed, Ready, Connecting, Suspended }
