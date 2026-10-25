using Quacktion.App.Models;

namespace Quacktion.Data.Agents;

// Tool names are the registrations in QuacktionAI's Calendar, Gmail and Meet MCP servers.
// Discovery still decides whether any one of them is executable for a connected user.
internal sealed record McpSkillTemplate(string Id, string Service, string? Tool, SkillPermissionLevel Permission,
    string Mode = "direct", string[]? AllowedArguments = null, string? UnavailableReason = null,
    string[]? Steps = null);

internal static class McpSkillCatalog
{
    public static readonly IReadOnlyDictionary<string, McpSkillTemplate> All = Build();

    private static IReadOnlyDictionary<string, McpSkillTemplate> Build()
    {
        var items = new Dictionary<string, McpSkillTemplate>(StringComparer.Ordinal);
        void Add(string service, string ids, string tool, SkillPermissionLevel permission = SkillPermissionLevel.Read,
            string mode = "direct", string? allowed = null)
        {
            foreach (var id in ids.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var full = service + "." + id;
                items.Add(full, new(full, service, tool, permission, mode,
                    allowed?.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
            }
        }
        void Missing(string service, string ids, string reason, SkillPermissionLevel permission)
        {
            foreach (var id in ids.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var full = service + "." + id;
                items.Add(full, new(full, service, null, permission, UnavailableReason: reason));
            }
        }
        void Workflow(string id, string steps, SkillPermissionLevel permission, string? unavailableReason = null)
        {
            var full = "workflow." + id;
            items.Add(full, new(full, "workflow", null, permission, "workflow",
                UnavailableReason: unavailableReason, Steps: steps.Split(' ')));
        }

        Add("calendar", "list_calendars", "calendar_list_calendars");
        Add("calendar", "list_events", "calendar_list_events");
        Add("calendar", "search_events", "calendar_list_events", mode: "search", allowed: "calendarId query timeMin timeMax maxResults pageToken");
        Add("calendar", "get_today get_tomorrow get_week", "calendar_list_events", mode: "relative_range", allowed: "calendarId maxResults");
        Add("calendar", "get_upcoming_meetings", "calendar_list_events", mode: "upcoming", allowed: "calendarId maxResults");
        Add("calendar", "get_event get_meeting_details get_attendees", "calendar_get_event");
        Add("calendar", "check_availability", "calendar_free_busy");
        Add("calendar", "find_free_slots", "calendar_free_busy", mode: "free_slots", allowed: "timeMin timeMax calendarIds timeZone durationMinutes");
        Add("calendar", "find_conflicts", "calendar_list_events", mode: "conflicts", allowed: "calendarId timeMin timeMax maxResults");
        Add("calendar", "create_event", "calendar_create_event", SkillPermissionLevel.Write);
        Add("calendar", "create_meeting", "calendar_create_event", SkillPermissionLevel.Write, "create_meet");
        Add("calendar", "reschedule_event", "calendar_update_event", SkillPermissionLevel.Write, "direct", "calendarId eventId start end timeZone sendUpdates");
        Add("calendar", "update_event", "calendar_update_event", SkillPermissionLevel.Write, "direct",
            "calendarId eventId summary start end timeZone description location addMeetLink sendUpdates");
        Add("calendar", "rename_event", "calendar_update_event", SkillPermissionLevel.Write, "direct", "calendarId eventId summary sendUpdates");
        Add("calendar", "update_description", "calendar_update_event", SkillPermissionLevel.Write, "direct", "calendarId eventId description sendUpdates");
        Add("calendar", "update_location", "calendar_update_event", SkillPermissionLevel.Write, "direct", "calendarId eventId location sendUpdates");
        Add("calendar", "add_meet_link", "calendar_update_event", SkillPermissionLevel.Write, "add_meet", "calendarId eventId sendUpdates");
        Missing("calendar", "add_attendees",
            "The Calendar MCP replaces the entire attendee list; safe merge requires an atomic attendee update capability.", SkillPermissionLevel.Write);
        Missing("calendar", "remove_attendee",
            "The Calendar MCP replaces the entire attendee list; safe removal requires an atomic attendee update capability.", SkillPermissionLevel.Destructive);
        Add("calendar", "delete_event cancel_meeting", "calendar_delete_event", SkillPermissionLevel.Destructive, "delete_event");
        Missing("calendar", "create_recurring_event update_recurring_event delete_recurring_series",
            "The Calendar MCP does not expose recurrence or occurrence scope; changing a series safely is unavailable.", SkillPermissionLevel.Destructive);
        Missing("calendar", "set_reminder respond_to_invitation", "The Calendar MCP has no reminder or invitation response tool.", SkillPermissionLevel.Write);
        Missing("calendar", "bulk_delete_events clear_schedule", "The Calendar MCP has no atomic bulk delete operation.", SkillPermissionLevel.Destructive);

        Add("gmail", "list_messages search_messages get_unread get_recent get_from_sender get_important get_starred",
            "gmail_search_messages", mode: "search_filter");
        Add("gmail", "get_message get_attachments summarise_email find_action_items", "gmail_get_message");
        Add("gmail", "get_thread summarise_thread", "gmail_get_thread");
        Add("gmail", "get_labels", "gmail_list_labels");
        Add("gmail", "create_draft", "gmail_create_draft", SkillPermissionLevel.Write);
        Add("gmail", "send_email", "gmail_send_message", SkillPermissionLevel.ExternalCommunication);
        Missing("gmail", "reply reply_all",
            "The Gmail MCP resolves recipients inside the send tool, after approval; exact recipients cannot be bound to approval.", SkillPermissionLevel.ExternalCommunication);
        Add("gmail", "mark_read mark_unread star_message unstar_message add_label remove_label archive_message move_to_inbox",
            "gmail_modify_labels", SkillPermissionLevel.Write, "label_change", "id labelId");
        Add("gmail", "trash_message", "gmail_trash_message", SkillPermissionLevel.Destructive);
        Missing("gmail", "update_draft forward create_label create_filter",
            "The Gmail MCP does not expose the required draft update, forward, label creation or filter tool.", SkillPermissionLevel.Write);
        Missing("gmail", "delete_message delete_thread bulk_trash bulk_delete delete_draft delete_label",
            "The Gmail MCP has no matching permanent delete or bulk operation.", SkillPermissionLevel.Destructive);

        Add("meet", "get_meeting get_space get_meeting_link", "meet_get_space");
        Add("meet", "list_meetings get_meeting_records", "meet_list_conference_records");
        Add("meet", "get_participants", "meet_list_participants");
        Add("meet", "list_transcripts", "meet_list_transcripts");
        Add("meet", "get_transcripts", "meet_get_transcript_entries");
        Add("meet", "get_recordings", "meet_list_recordings");
        Add("meet", "get_conference_details", "meet_get_conference_record");
        Add("meet", "create_meeting create_meeting_link", "meet_create_space", SkillPermissionLevel.Write);
        Add("meet", "update_meeting configure_access", "meet_update_space_access", SkillPermissionLevel.Destructive);
        Add("meet", "restrict_access", "meet_update_space_access", SkillPermissionLevel.Destructive, "restrict_access", "space");
        Add("meet", "end_meeting", "meet_end_active_conference", SkillPermissionLevel.Destructive);
        Add("meet", "schedule_meeting", "calendar_create_event", SkillPermissionLevel.Write, "create_meet");
        Missing("meet", "invite_participants", "The Meet MCP has no invite tool, and Calendar attendee updates replace the full list.", SkillPermissionLevel.Write);
        Missing("meet", "delete_meeting remove_participant", "The Meet MCP has no delete-space or remove-participant tool.", SkillPermissionLevel.Destructive);

        Workflow("schedule_from_email", "gmail.search_messages gmail.get_message calendar.check_availability calendar.create_meeting", SkillPermissionLevel.Write);
        Workflow("email_meeting_summary", "calendar.get_attendees gmail.create_draft gmail.send_email", SkillPermissionLevel.ExternalCommunication,
            "Meeting-analysis retrieval and exact draft-to-send content binding are not implemented in this workflow.");
        Workflow("reschedule_and_notify", "calendar.search_events calendar.check_availability calendar.reschedule_event gmail.send_email", SkillPermissionLevel.ExternalCommunication);
        Workflow("create_video_meeting", "calendar.check_availability calendar.create_meeting", SkillPermissionLevel.Write);
        Workflow("find_meeting_link", "calendar.get_upcoming_meetings calendar.get_meeting_details", SkillPermissionLevel.Read);
        Workflow("cancel_meeting", "calendar.get_event calendar.delete_event", SkillPermissionLevel.Destructive);
        Workflow("organise_meeting", "calendar.check_availability calendar.create_meeting", SkillPermissionLevel.Write,
            "The gateway has no contact-resolution tool for participant names.");
        Workflow("prepare_for_meeting", "calendar.get_upcoming_meetings gmail.search_messages", SkillPermissionLevel.Read,
            "Previous meeting notes and action-item retrieval are not connected to this workflow.");
        Workflow("follow_up_after_meeting", "gmail.create_draft gmail.send_email", SkillPermissionLevel.ExternalCommunication,
            "Meeting-analysis retrieval and action-item execution are not connected to this workflow.");
        return items;
    }
}
