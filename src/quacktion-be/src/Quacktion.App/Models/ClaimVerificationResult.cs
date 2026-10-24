using System.Text.Json.Serialization;
namespace Quacktion.App.Models;

public record ClaimVerificationResult(VerificationAssessment Assessment, IReadOnlyList<Evidence> Evidence);
