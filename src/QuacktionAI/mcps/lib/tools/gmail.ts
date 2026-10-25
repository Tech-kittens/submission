import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { GoogleClient } from "@/lib/google";
import { run } from "@/lib/mcp";

const API = "https://gmail.googleapis.com/gmail/v1/users/me";

interface Part {
  mimeType?: string;
  filename?: string;
  headers?: { name: string; value: string }[];
  body?: { data?: string; size?: number; attachmentId?: string };
  parts?: Part[];
}
interface Message {
  id: string;
  threadId: string;
  labelIds?: string[];
  snippet?: string;
  payload?: Part;
}

const header = (m: Message, name: string) =>
  m.payload?.headers?.find((h) => h.name.toLowerCase() === name.toLowerCase())?.value;

const decode = (data?: string) => (data ? Buffer.from(data, "base64url").toString("utf8") : "");

const htmlToText = (html: string) =>
  html
    .replace(/<(style|script)[\s\S]*?<\/\1>/gi, "")
    .replace(/<br\s*\/?>|<\/(p|div|tr|li|h\d)>/gi, "\n")
    .replace(/<[^>]+>/g, "")
    .replace(/&nbsp;/g, " ")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/\n{3,}/g, "\n\n")
    .trim();

interface Parsed {
  text: string[];
  html: string[];
  attachments: { filename: string; mimeType?: string; size?: number; attachmentId: string }[];
}

function walk(part: Part | undefined, out: Parsed) {
  if (!part) return;
  if (part.filename && part.body?.attachmentId) {
    out.attachments.push({
      filename: part.filename,
      mimeType: part.mimeType,
      size: part.body.size,
      attachmentId: part.body.attachmentId,
    });
  } else if (part.mimeType === "text/plain" && part.body?.data) {
    out.text.push(decode(part.body.data));
  } else if (part.mimeType === "text/html" && part.body?.data) {
    out.html.push(decode(part.body.data));
  }
  part.parts?.forEach((p) => walk(p, out));
}

const MAX_BODY = 50_000;

function brief(m: Message) {
  return {
    id: m.id,
    threadId: m.threadId,
    from: header(m, "From"),
    to: header(m, "To"),
    subject: header(m, "Subject"),
    date: header(m, "Date"),
    snippet: m.snippet,
    labels: m.labelIds,
  };
}

function full(m: Message) {
  const parsed: Parsed = { text: [], html: [], attachments: [] };
  walk(m.payload, parsed);
  const body = parsed.text.join("\n") || htmlToText(parsed.html.join("\n"));
  return {
    ...brief(m),
    cc: header(m, "Cc"),
    messageIdHeader: header(m, "Message-ID"),
    body: body.length > MAX_BODY ? `${body.slice(0, MAX_BODY)}\n…[truncated]` : body,
    attachments: parsed.attachments,
  };
}

const getMeta = (g: GoogleClient, id: string) =>
  g.request<Message>("GET", `${API}/messages/${id}`, {
    query: { format: "metadata", metadataHeaders: ["From", "To", "Cc", "Subject", "Date"] },
  });

const encodeHeader = (value: string) =>
  /^[\x20-\x7e]*$/.test(value) ? value : `=?UTF-8?B?${Buffer.from(value).toString("base64")}?=`;

function buildRaw(o: {
  to: string[];
  cc?: string[];
  bcc?: string[];
  subject: string;
  body: string;
  html?: boolean;
  inReplyTo?: string;
  references?: string;
}) {
  const lines = [
    `To: ${o.to.join(", ")}`,
    ...(o.cc?.length ? [`Cc: ${o.cc.join(", ")}`] : []),
    ...(o.bcc?.length ? [`Bcc: ${o.bcc.join(", ")}`] : []),
    `Subject: ${encodeHeader(o.subject)}`,
    "MIME-Version: 1.0",
    `Content-Type: text/${o.html ? "html" : "plain"}; charset="UTF-8"`,
    "Content-Transfer-Encoding: base64",
    ...(o.inReplyTo ? [`In-Reply-To: ${o.inReplyTo}`] : []),
    ...(o.references ? [`References: ${o.references}`] : []),
    "",
    Buffer.from(o.body, "utf8").toString("base64").replace(/(.{76})/g, "$1\r\n"),
  ];
  return Buffer.from(lines.join("\r\n"), "utf8").toString("base64url");
}

const emails = z.array(z.string()).describe("Email addresses");

export function registerGmailTools(server: McpServer) {
  server.registerTool(
    "gmail_get_profile",
    { title: "Gmail profile", description: "Get the connected Gmail address and mailbox totals.", inputSchema: {} },
    async (_a, extra) => run(extra, (g) => g.request("GET", `${API}/profile`)),
  );

  server.registerTool(
    "gmail_search_messages",
    {
      title: "Search Gmail",
      description:
        "Search messages with Gmail search syntax (e.g. 'from:alice is:unread newer_than:7d has:attachment'). Returns brief summaries; use gmail_get_message for the body.",
      inputSchema: {
        query: z.string().describe("Gmail search query. Empty string lists recent mail."),
        maxResults: z.number().int().min(1).max(50).default(10),
        pageToken: z.string().optional(),
        labelIds: z.array(z.string()).optional().describe("Only messages with all of these label IDs"),
      },
    },
    async (a, extra) =>
      run(extra, async (g) => {
        const list = await g.request<{ messages?: { id: string }[]; nextPageToken?: string }>(
          "GET",
          `${API}/messages`,
          { query: { q: a.query, maxResults: a.maxResults, pageToken: a.pageToken, labelIds: a.labelIds } },
        );
        const messages = await Promise.all((list.messages ?? []).map((m) => getMeta(g, m.id)));
        return { messages: messages.map(brief), nextPageToken: list.nextPageToken };
      }),
  );

  server.registerTool(
    "gmail_get_message",
    {
      title: "Read an email",
      description: "Get one message with its decoded body and attachment list.",
      inputSchema: { id: z.string().describe("Message ID") },
    },
    async ({ id }, extra) =>
      run(extra, async (g) => full(await g.request<Message>("GET", `${API}/messages/${id}`, { query: { format: "full" } }))),
  );

  server.registerTool(
    "gmail_get_thread",
    {
      title: "Read a thread",
      description: "Get every message in a thread, oldest first, with decoded bodies.",
      inputSchema: { threadId: z.string() },
    },
    async ({ threadId }, extra) =>
      run(extra, async (g) => {
        const thread = await g.request<{ id: string; messages: Message[] }>("GET", `${API}/threads/${threadId}`, {
          query: { format: "full" },
        });
        return { id: thread.id, messages: thread.messages.map(full) };
      }),
  );

  server.registerTool(
    "gmail_send_message",
    {
      title: "Send an email",
      description: "Send a new email from the connected account. This is irreversible; confirm with the user first.",
      inputSchema: {
        to: emails,
        subject: z.string(),
        body: z.string(),
        cc: emails.optional(),
        bcc: emails.optional(),
        html: z.boolean().default(false).describe("Treat body as HTML"),
      },
    },
    async (a, extra) =>
      run(extra, async (g) => {
        const sent = await g.request<{ id: string; threadId: string }>("POST", `${API}/messages/send`, {
          body: { raw: buildRaw(a) },
        });
        return { sent: true, id: sent.id, threadId: sent.threadId };
      }),
  );

  server.registerTool(
    "gmail_reply_to_message",
    {
      title: "Reply to an email",
      description: "Reply within the original thread. Set replyAll to include the original To/Cc recipients.",
      inputSchema: {
        messageId: z.string().describe("ID of the message being replied to"),
        body: z.string(),
        replyAll: z.boolean().default(false),
        html: z.boolean().default(false),
      },
    },
    async (a, extra) =>
      run(extra, async (g) => {
        const [original, profile] = await Promise.all([
          g.request<Message>("GET", `${API}/messages/${a.messageId}`, {
            query: { format: "metadata", metadataHeaders: ["From", "To", "Cc", "Subject", "Message-ID", "References", "Reply-To"] },
          }),
          g.request<{ emailAddress: string }>("GET", `${API}/profile`),
        ]);
        const self = profile.emailAddress.toLowerCase();
        const split = (v?: string) => (v ? v.split(",").map((s) => s.trim()).filter(Boolean) : []);
        const to = split(header(original, "Reply-To") ?? header(original, "From"));
        const cc = a.replyAll
          ? [...split(header(original, "To")), ...split(header(original, "Cc"))].filter(
              (addr) => !addr.toLowerCase().includes(self),
            )
          : [];
        const subject = header(original, "Subject") ?? "";
        const messageId = header(original, "Message-ID");
        const references = [header(original, "References"), messageId].filter(Boolean).join(" ");
        const sent = await g.request<{ id: string; threadId: string }>("POST", `${API}/messages/send`, {
          body: {
            threadId: original.threadId,
            raw: buildRaw({
              to,
              cc,
              subject: /^re:/i.test(subject) ? subject : `Re: ${subject}`,
              body: a.body,
              html: a.html,
              inReplyTo: messageId,
              references,
            }),
          },
        });
        return { sent: true, id: sent.id, threadId: sent.threadId, to, cc };
      }),
  );

  server.registerTool(
    "gmail_create_draft",
    {
      title: "Create a draft",
      description: "Save a draft without sending it.",
      inputSchema: {
        to: emails,
        subject: z.string(),
        body: z.string(),
        cc: emails.optional(),
        bcc: emails.optional(),
        html: z.boolean().default(false),
      },
    },
    async (a, extra) =>
      run(extra, async (g) => {
        const draft = await g.request<{ id: string; message: { id: string } }>("POST", `${API}/drafts`, {
          body: { message: { raw: buildRaw(a) } },
        });
        return { draftId: draft.id, messageId: draft.message.id };
      }),
  );

  server.registerTool(
    "gmail_send_draft",
    {
      title: "Send a draft",
      description: "Send an existing draft. Irreversible; confirm first.",
      inputSchema: { draftId: z.string() },
    },
    async ({ draftId }, extra) =>
      run(extra, (g) => g.request("POST", `${API}/drafts/send`, { body: { id: draftId } })),
  );

  server.registerTool(
    "gmail_list_labels",
    { title: "List labels", description: "List all labels (system and user) with IDs.", inputSchema: {} },
    async (_a, extra) => run(extra, (g) => g.request("GET", `${API}/labels`)),
  );

  server.registerTool(
    "gmail_modify_labels",
    {
      title: "Add/remove labels",
      description:
        "Change labels on a message. Mark read: remove UNREAD. Archive: remove INBOX. Star: add STARRED.",
      inputSchema: {
        id: z.string(),
        addLabelIds: z.array(z.string()).default([]),
        removeLabelIds: z.array(z.string()).default([]),
      },
    },
    async ({ id, addLabelIds, removeLabelIds }, extra) =>
      run(extra, async (g) => {
        const m = await g.request<Message>("POST", `${API}/messages/${id}/modify`, {
          body: { addLabelIds, removeLabelIds },
        });
        return { id: m.id, labels: m.labelIds };
      }),
  );

  server.registerTool(
    "gmail_trash_message",
    {
      title: "Move to trash",
      description: "Move a message to Trash (recoverable for 30 days).",
      inputSchema: { id: z.string() },
    },
    async ({ id }, extra) =>
      run(extra, async (g) => {
        await g.request("POST", `${API}/messages/${id}/trash`);
        return { trashed: id };
      }),
  );
}
