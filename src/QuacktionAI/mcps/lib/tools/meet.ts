import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { run } from "@/lib/mcp";

const API = "https://meet.googleapis.com/v2";

/** Accept either a bare ID/meeting code or a full resource name. */
const name = (prefix: string, value: string) => (value.startsWith(`${prefix}/`) ? value : `${prefix}/${value}`);

const accessType = z
  .enum(["OPEN", "TRUSTED", "RESTRICTED"])
  .describe("OPEN: anyone with the link joins. TRUSTED: organization members + invitees. RESTRICTED: invitees only.");

export function registerMeetTools(server: McpServer) {
  server.registerTool(
    "meet_create_space",
    {
      title: "Create a meeting",
      description: "Create a new Google Meet space and return its join link.",
      inputSchema: { accessType: accessType.optional() },
    },
    async (a, extra) =>
      run(extra, (g) =>
        g.request("POST", `${API}/spaces`, { body: a.accessType ? { config: { accessType: a.accessType } } : {} }),
      ),
  );

  server.registerTool(
    "meet_get_space",
    {
      title: "Get a meeting space",
      description: "Look up a space by ID or meeting code (e.g. abc-mnop-xyz).",
      inputSchema: { space: z.string() },
    },
    async ({ space }, extra) => run(extra, (g) => g.request("GET", `${API}/${name("spaces", space)}`)),
  );

  server.registerTool(
    "meet_update_space_access",
    {
      title: "Change meeting access",
      description: "Change who can join a space you created.",
      inputSchema: { space: z.string(), accessType },
    },
    async (a, extra) =>
      run(extra, (g) =>
        g.request("PATCH", `${API}/${name("spaces", a.space)}`, {
          query: { updateMask: "config.accessType" },
          body: { config: { accessType: a.accessType } },
        }),
      ),
  );

  server.registerTool(
    "meet_end_active_conference",
    {
      title: "End the active call",
      description: "End the call currently running in a space, removing all participants.",
      inputSchema: { space: z.string() },
    },
    async ({ space }, extra) =>
      run(extra, async (g) => {
        await g.request("POST", `${API}/${name("spaces", space)}:endActiveConference`, { body: {} });
        return { ended: space };
      }),
  );

  server.registerTool(
    "meet_list_conference_records",
    {
      title: "List past meetings",
      description:
        "List conference records (meeting sessions), newest first. Optional filter, e.g. space.meeting_code = \"abc-mnop-xyz\" or start_time>=\"2026-10-01T00:00:00Z\".",
      inputSchema: {
        filter: z.string().optional(),
        pageSize: z.number().int().min(1).max(100).default(20),
        pageToken: z.string().optional(),
      },
    },
    async (a, extra) =>
      run(extra, (g) =>
        g.request("GET", `${API}/conferenceRecords`, {
          query: { filter: a.filter, pageSize: a.pageSize, pageToken: a.pageToken },
        }),
      ),
  );

  server.registerTool(
    "meet_get_conference_record",
    {
      title: "Get a past meeting",
      description: "Get one conference record by ID or resource name.",
      inputSchema: { conferenceRecord: z.string() },
    },
    async ({ conferenceRecord }, extra) =>
      run(extra, (g) => g.request("GET", `${API}/${name("conferenceRecords", conferenceRecord)}`)),
  );

  server.registerTool(
    "meet_list_participants",
    {
      title: "List participants",
      description: "Who joined a conference, with join/leave times.",
      inputSchema: { conferenceRecord: z.string(), pageSize: z.number().int().min(1).max(250).default(100), pageToken: z.string().optional() },
    },
    async (a, extra) =>
      run(extra, (g) =>
        g.request("GET", `${API}/${name("conferenceRecords", a.conferenceRecord)}/participants`, {
          query: { pageSize: a.pageSize, pageToken: a.pageToken },
        }),
      ),
  );

  server.registerTool(
    "meet_list_recordings",
    {
      title: "List recordings",
      description: "Recordings of a conference (stored in the organizer's Google Drive).",
      inputSchema: { conferenceRecord: z.string() },
    },
    async ({ conferenceRecord }, extra) =>
      run(extra, (g) => g.request("GET", `${API}/${name("conferenceRecords", conferenceRecord)}/recordings`)),
  );

  server.registerTool(
    "meet_list_transcripts",
    {
      title: "List transcripts",
      description: "Transcripts of a conference. Use the returned 'name' with meet_get_transcript_entries.",
      inputSchema: { conferenceRecord: z.string() },
    },
    async ({ conferenceRecord }, extra) =>
      run(extra, (g) => g.request("GET", `${API}/${name("conferenceRecords", conferenceRecord)}/transcripts`)),
  );

  server.registerTool(
    "meet_get_transcript_entries",
    {
      title: "Read a transcript",
      description: "Spoken lines of a transcript, in order. transcript is the full name: conferenceRecords/{id}/transcripts/{id}.",
      inputSchema: { transcript: z.string(), pageSize: z.number().int().min(1).max(100).default(100), pageToken: z.string().optional() },
    },
    async (a, extra) =>
      run(extra, (g) =>
        g.request("GET", `${API}/${a.transcript}/entries`, { query: { pageSize: a.pageSize, pageToken: a.pageToken } }),
      ),
  );
}
