import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { randomUUID } from "node:crypto";
import { z } from "zod";
import { run } from "@/lib/mcp";

const API = "https://www.googleapis.com/calendar/v3";

const when = (value: string, timeZone?: string) =>
  /^\d{4}-\d{2}-\d{2}$/.test(value) ? { date: value } : { dateTime: value, ...(timeZone ? { timeZone } : {}) };

const cal = (id: string) => encodeURIComponent(id);

const meetRequest = () => ({
  createRequest: { requestId: randomUUID(), conferenceSolutionKey: { type: "hangoutsMeet" } },
});

const sendUpdates = z.enum(["all", "externalOnly", "none"]).default("all").describe("Who gets email notifications");

interface EventLike {
  id?: string;
  summary?: string;
  description?: string;
  location?: string;
  status?: string;
  htmlLink?: string;
  hangoutLink?: string;
  start?: unknown;
  end?: unknown;
  attendees?: { email: string; responseStatus?: string; optional?: boolean }[];
  organizer?: { email?: string };
}

const slim = (e: EventLike) => ({
  id: e.id,
  summary: e.summary,
  description: e.description,
  location: e.location,
  status: e.status,
  start: e.start,
  end: e.end,
  meetLink: e.hangoutLink,
  link: e.htmlLink,
  organizer: e.organizer?.email,
  attendees: e.attendees?.map((a) => ({ email: a.email, response: a.responseStatus, optional: a.optional })),
});

export function registerCalendarTools(server: McpServer) {
  server.registerTool(
    "calendar_list_calendars",
    { title: "List calendars", description: "List calendars the user can see.", inputSchema: {} },
    async (_a, extra) =>
      run(extra, async (g) => {
        const res = await g.request<{ items?: { id: string; summary?: string; primary?: boolean; accessRole?: string; timeZone?: string }[] }>(
          "GET",
          `${API}/users/me/calendarList`,
        );
        return (res.items ?? []).map((c) => ({
          id: c.id,
          name: c.summary,
          primary: c.primary ?? false,
          role: c.accessRole,
          timeZone: c.timeZone,
        }));
      }),
  );

  server.registerTool(
    "calendar_list_events",
    {
      title: "List events",
      description: "List events in a time range, soonest first. Times are RFC3339 (e.g. 2026-10-26T00:00:00Z).",
      inputSchema: {
        calendarId: z.string().default("primary"),
        timeMin: z.string().optional(),
        timeMax: z.string().optional(),
        query: z.string().optional().describe("Free-text filter"),
        maxResults: z.number().int().min(1).max(250).default(25),
        pageToken: z.string().optional(),
      },
    },
    async (a, extra) =>
      run(extra, async (g) => {
        const res = await g.request<{ items?: EventLike[]; nextPageToken?: string }>(
          "GET",
          `${API}/calendars/${cal(a.calendarId)}/events`,
          {
            query: {
              timeMin: a.timeMin ?? new Date().toISOString(),
              timeMax: a.timeMax,
              q: a.query,
              maxResults: a.maxResults,
              pageToken: a.pageToken,
              singleEvents: true,
              orderBy: "startTime",
            },
          },
        );
        return { events: (res.items ?? []).map(slim), nextPageToken: res.nextPageToken };
      }),
  );

  server.registerTool(
    "calendar_get_event",
    {
      title: "Get event",
      description: "Get one event by ID.",
      inputSchema: { calendarId: z.string().default("primary"), eventId: z.string() },
    },
    async (a, extra) =>
      run(extra, async (g) =>
        slim(await g.request<EventLike>("GET", `${API}/calendars/${cal(a.calendarId)}/events/${a.eventId}`)),
      ),
  );

  server.registerTool(
    "calendar_create_event",
    {
      title: "Create event",
      description:
        "Create an event. Use RFC3339 date-times with offset (2026-10-26T15:00:00+01:00) or YYYY-MM-DD for all-day. Set addMeetLink for a Google Meet link.",
      inputSchema: {
        calendarId: z.string().default("primary"),
        summary: z.string(),
        start: z.string(),
        end: z.string(),
        timeZone: z.string().optional().describe("IANA zone, e.g. Europe/London; needed if start/end have no offset"),
        description: z.string().optional(),
        location: z.string().optional(),
        attendees: z.array(z.string()).optional().describe("Attendee emails"),
        addMeetLink: z.boolean().default(false),
        sendUpdates,
      },
    },
    async (a, extra) =>
      run(extra, async (g) =>
        slim(
          await g.request<EventLike>("POST", `${API}/calendars/${cal(a.calendarId)}/events`, {
            query: { conferenceDataVersion: a.addMeetLink ? 1 : 0, sendUpdates: a.sendUpdates },
            body: {
              summary: a.summary,
              description: a.description,
              location: a.location,
              start: when(a.start, a.timeZone),
              end: when(a.end, a.timeZone),
              attendees: a.attendees?.map((email) => ({ email })),
              ...(a.addMeetLink ? { conferenceData: meetRequest() } : {}),
            },
          }),
        ),
      ),
  );

  server.registerTool(
    "calendar_update_event",
    {
      title: "Update event",
      description: "Change fields of an event. Only the fields you pass are changed. 'attendees' replaces the list.",
      inputSchema: {
        calendarId: z.string().default("primary"),
        eventId: z.string(),
        summary: z.string().optional(),
        start: z.string().optional(),
        end: z.string().optional(),
        timeZone: z.string().optional(),
        description: z.string().optional(),
        location: z.string().optional(),
        attendees: z.array(z.string()).optional(),
        addMeetLink: z.boolean().default(false),
        sendUpdates,
      },
    },
    async (a, extra) =>
      run(extra, async (g) =>
        slim(
          await g.request<EventLike>("PATCH", `${API}/calendars/${cal(a.calendarId)}/events/${a.eventId}`, {
            query: { conferenceDataVersion: a.addMeetLink ? 1 : 0, sendUpdates: a.sendUpdates },
            body: {
              summary: a.summary,
              description: a.description,
              location: a.location,
              start: a.start ? when(a.start, a.timeZone) : undefined,
              end: a.end ? when(a.end, a.timeZone) : undefined,
              attendees: a.attendees?.map((email) => ({ email })),
              ...(a.addMeetLink ? { conferenceData: meetRequest() } : {}),
            },
          }),
        ),
      ),
  );

  server.registerTool(
    "calendar_delete_event",
    {
      title: "Delete event",
      description: "Delete an event. Irreversible; confirm first.",
      inputSchema: { calendarId: z.string().default("primary"), eventId: z.string(), sendUpdates },
    },
    async (a, extra) =>
      run(extra, async (g) => {
        await g.request("DELETE", `${API}/calendars/${cal(a.calendarId)}/events/${a.eventId}`, {
          query: { sendUpdates: a.sendUpdates },
        });
        return { deleted: a.eventId };
      }),
  );

  server.registerTool(
    "calendar_quick_add",
    {
      title: "Quick add event",
      description: "Create an event from natural language, e.g. 'Lunch with Sam tomorrow at 1pm'.",
      inputSchema: { calendarId: z.string().default("primary"), text: z.string() },
    },
    async (a, extra) =>
      run(extra, async (g) =>
        slim(await g.request<EventLike>("POST", `${API}/calendars/${cal(a.calendarId)}/events/quickAdd`, { query: { text: a.text } })),
      ),
  );

  server.registerTool(
    "calendar_free_busy",
    {
      title: "Free/busy",
      description: "Get busy intervals for one or more calendars (use attendee emails to find shared free time).",
      inputSchema: {
        timeMin: z.string(),
        timeMax: z.string(),
        calendarIds: z.array(z.string()).default(["primary"]),
        timeZone: z.string().optional(),
      },
    },
    async (a, extra) =>
      run(extra, async (g) => {
        const res = await g.request<{ calendars: Record<string, { busy: { start: string; end: string }[]; errors?: unknown[] }> }>(
          "POST",
          `${API}/freeBusy`,
          { body: { timeMin: a.timeMin, timeMax: a.timeMax, timeZone: a.timeZone, items: a.calendarIds.map((id) => ({ id })) } },
        );
        return res.calendars;
      }),
  );
}
