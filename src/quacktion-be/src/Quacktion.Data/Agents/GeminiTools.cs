namespace Quacktion.Data.Agents;

public static class GeminiTools
{
    private static object QueryParameters(bool action) => new
    {
        type = "OBJECT",
        properties = action ? new Dictionary<string, object>
        {
            ["actionType"] = new { type = "STRING", @enum = App.Services.MeetingToolService.Capabilities },
            ["query"] = new { type = "STRING", description = "Specific task request; no invented business-system facts." }
        } : new Dictionary<string, object> { ["query"] = new { type = "STRING" } },
        required = action ? new[] { "actionType", "query" } : ["query"]
    };
    private static object IdParameters => new { type = "OBJECT", properties = new { quacktionId = new { type = "STRING" } }, required = new[] { "quacktionId" } };
    public static readonly FunctionDeclaration[] Declarations =
    [
        new("meeting_briefing", "Use history with a focused query when the user asks about previous meetings, past discussions, decisions, or commitments. Search is limited to the authenticated owner's completed meeting memory. Use upcoming to list future meetings before choosing a named one; read/details retrieves its saved briefing. Refresh queues background research; dismiss disables the reminder. Never use a bare yes as permission unless the backend reports an accepted briefing offer. These operations never approve protected actions.", new
        { type = "OBJECT", properties = new { operation = new { type = "STRING", @enum = new[] { "upcoming", "history", "read", "details", "refresh", "dismiss" } }, meetingId = new { type = "STRING" }, query = new { type = "STRING" } }, required = new[] { "operation" } }),
        new("start_meeting", "Start Meeting Mode only after the owner explicitly confirms recording and transcription consent and participant notice. Never infer consent from a casual request.", new
        { type = "OBJECT", properties = new { recordingConsent = new { type = "BOOLEAN" }, name = new { type = "STRING" } }, required = new[] { "recordingConsent" } }),
        new("end_meeting", "End the active meeting for this authenticated device. Then ask the user whether they want to hear a summary of background work, and wait for their answer. If they agree, summarize completed, in-progress, and failed work and clearly list each task still awaiting their approval. The request to hear a summary does not grant action approval or permission to disclose protected task results.", new
        { type = "OBJECT", properties = new { } }),
        new("execute_quacktion", "Use for GitHub repository, issue and pull request tasks and Google Calendar, Gmail, and Meet requests, including checking availability, finding events, creating or changing events, and email tasks. During a meeting, proactively investigate reported bugs using read-only GitHub repository tools, even without a direct request. Never modify code or write to a repository without exact owner approval. The backend selects an unambiguous connected account or asks the owner to choose one. Protected actions require exact owner approval. Work proceeds in the background; report only backend-confirmed results.", new
        {
            type = "OBJECT", properties = new { request = new { type = "STRING", description = "The user's intended outcome, without account IDs or credentials." }, announcementPolicy = new { type = "STRING", @enum = new[] { "ANNOUNCE_WHEN_IDLE", "ANNOUNCE_IMMEDIATELY", "PRIVATE_NOTIFICATION", "APPROVAL_REQUIRED" } } }, required = new[] { "request" }
        }),
        new("create_quacktion", "Create a durable background task. Use internal.preparation for upcoming meeting briefings, previous discussions, suggested questions and outstanding commitments. Also supports summary, analysis, suggestions, web research, or enabled read-only Google MCP tasks. Continues during the meeting. Completion is reported without disclosing private results; request approval to announce them.", QueryParameters(true)),
        new("get_quacktion_status", "Read a task status in this meeting. Does not disclose confidential results.", IdParameters),
        new("resolve_voice_approval", "Resolve a pending approval by voice after you have stated the exact action and the owner has explicitly said yes to that action, or explicitly rejected it. Never call this for a generic yes, for consent to hear a summary, or before describing what will happen. The backend checks the authenticated owner, task, current approval and action digest.", new
        { type = "OBJECT", properties = new { quacktionId = new { type = "STRING" }, approve = new { type = "BOOLEAN" } }, required = new[] { "quacktionId", "approve" } }),
        new("announce_quacktion_result", "Request explicit owner approval to announce a completed task's exact result. Waits asynchronously for approval; never grants approval.", IdParameters),
        new("request_quacktion_approval", "Request owner approval to share a completed task result. The frontend must approve or deny it.", IdParameters),
        new("research_external_information", "Research a public question using Google Search grounding in a durable background task. No access to private business systems.", QueryParameters(false))
    ];
}
