import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { runJira as run } from "@/lib/mcp";

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type J = Record<string, any>;

const MAX_TEXT = 50_000;
const trunc = (s: string | undefined, max = MAX_TEXT) => (s && s.length > max ? `${s.slice(0, max)}\n…[truncated]` : s);
const enc = encodeURIComponent;

/** Plain text -> Atlassian Document Format (Jira Cloud v3 requires ADF for descriptions and comments). */
const toAdf = (text: string) => ({
  type: "doc",
  version: 1,
  content: text.split(/\n{2,}/).map((para) => {
    const lines = para.split("\n");
    const content = lines.flatMap((line, i) => [
      ...(line ? [{ type: "text", text: line }] : []),
      ...(i < lines.length - 1 ? [{ type: "hardBreak" }] : []),
    ]);
    return { type: "paragraph", content };
  }),
});

/** ADF -> plain text, good enough for reading descriptions and comments. */
function fromAdf(node: J | string | null | undefined): string {
  if (!node) return "";
  if (typeof node === "string") return node;
  switch (node.type) {
    case "text":
      return node.text ?? "";
    case "hardBreak":
      return "\n";
    case "mention":
      return node.attrs?.text ?? "";
    case "inlineCard":
      return node.attrs?.url ?? "";
    case "emoji":
      return node.attrs?.text ?? node.attrs?.shortName ?? "";
    case "listItem":
      return `- ${(node.content ?? []).map(fromAdf).join("").trim()}\n`;
    case "codeBlock":
      return `\`\`\`\n${(node.content ?? []).map(fromAdf).join("")}\n\`\`\`\n`;
  }
  const inner = (node.content ?? []).map(fromAdf).join("");
  return ["paragraph", "heading", "blockquote", "panel"].includes(node.type) ? `${inner}\n` : inner;
}

const text = (adf: J | string | null | undefined) => trunc(fromAdf(adf).trim() || undefined);

const ISSUE_FIELDS = ["summary", "status", "issuetype", "priority", "assignee", "reporter", "labels", "project", "created", "updated"];

const slimIssue = (site: string, i: J, withBody = false) => ({
  key: i.key,
  summary: i.fields?.summary,
  status: i.fields?.status?.name,
  type: i.fields?.issuetype?.name,
  priority: i.fields?.priority?.name,
  project: i.fields?.project?.key,
  assignee: i.fields?.assignee ? { name: i.fields.assignee.displayName, accountId: i.fields.assignee.accountId } : null,
  reporter: i.fields?.reporter?.displayName,
  labels: i.fields?.labels,
  created: i.fields?.created,
  updated: i.fields?.updated,
  url: `${site}/browse/${i.key}`,
  ...(withBody ? { description: text(i.fields?.description) } : {}),
});

const slimComment = (c: J) => ({
  id: c.id,
  author: c.author?.displayName,
  created: c.created,
  updated: c.updated,
  body: text(c.body),
});

const slimUser = (u: J) => ({
  accountId: u.accountId,
  name: u.displayName,
  email: u.emailAddress,
  active: u.active,
});

const keyShape = { key: z.string().describe("Issue key, e.g. ABC-123") };

export function registerJiraTools(server: McpServer) {
  server.registerTool(
    "jira_get_me",
    { title: "Who am I", description: "Get the connected Jira user and site.", inputSchema: {} },
    async (_a, extra) =>
      run(extra, async (jira) => {
        const u = await jira.request<J>("GET", "/rest/api/3/myself");
        return { ...slimUser(u), site: jira.site };
      }),
  );

  server.registerTool(
    "jira_list_projects",
    {
      title: "List projects",
      description: "List Jira projects you can see, optionally filtered by a name/key search.",
      inputSchema: {
        query: z.string().optional().describe("Filter by project name or key"),
        maxResults: z.number().int().min(1).max(100).default(50),
      },
    },
    async (a, extra) =>
      run(extra, async (jira) => {
        const res = await jira.request<J>("GET", "/rest/api/3/project/search", {
          query: { query: a.query, maxResults: a.maxResults },
        });
        return (res.values ?? []).map((p: J) => ({ key: p.key, name: p.name, type: p.projectTypeKey, lead: p.lead?.displayName }));
      }),
  );

  server.registerTool(
    "jira_search_issues",
    {
      title: "Search issues (JQL)",
      description:
        "Search issues with JQL, e.g. 'assignee = currentUser() AND resolution = Unresolved ORDER BY updated DESC' or 'project = ABC AND status = \"In Progress\"'. Pass nextPageToken from a previous result for more.",
      inputSchema: {
        jql: z.string(),
        maxResults: z.number().int().min(1).max(100).default(25),
        nextPageToken: z.string().optional(),
      },
    },
    async (a, extra) =>
      run(extra, async (jira) => {
        const res = await jira.request<J>("POST", "/rest/api/3/search/jql", {
          body: { jql: a.jql, maxResults: a.maxResults, fields: ISSUE_FIELDS, nextPageToken: a.nextPageToken },
        });
        return {
          issues: (res.issues ?? []).map((i: J) => slimIssue(jira.site, i)),
          nextPageToken: res.nextPageToken,
          isLast: res.isLast,
        };
      }),
  );

  server.registerTool(
    "jira_get_issue",
    { title: "Get an issue", description: "Get one issue with its description.", inputSchema: keyShape },
    async (a, extra) =>
      run(extra, async (jira) =>
        slimIssue(jira.site, await jira.request<J>("GET", `/rest/api/3/issue/${enc(a.key)}`, { query: { fields: [...ISSUE_FIELDS, "description"].join(",") } }), true),
      ),
  );

  server.registerTool(
    "jira_create_issue",
    {
      title: "Create an issue",
      description: "Create an issue. Visible to others immediately; confirm with the user first.",
      inputSchema: {
        project: z.string().describe("Project key, e.g. ABC"),
        summary: z.string(),
        issueType: z.string().default("Task").describe("e.g. Task, Bug, Story, Epic"),
        description: z.string().optional().describe("Plain text; blank lines make paragraphs"),
        priority: z.string().optional().describe("e.g. High, Medium, Low"),
        labels: z.array(z.string()).optional(),
        assigneeAccountId: z.string().optional().describe("From jira_search_users"),
      },
    },
    async (a, extra) =>
      run(extra, async (jira) => {
        const res = await jira.request<J>("POST", "/rest/api/3/issue", {
          body: {
            fields: {
              project: { key: a.project },
              summary: a.summary,
              issuetype: { name: a.issueType },
              ...(a.description ? { description: toAdf(a.description) } : {}),
              ...(a.priority ? { priority: { name: a.priority } } : {}),
              ...(a.labels ? { labels: a.labels } : {}),
              ...(a.assigneeAccountId ? { assignee: { accountId: a.assigneeAccountId } } : {}),
            },
          },
        });
        return { key: res.key, url: `${jira.site}/browse/${res.key}` };
      }),
  );

  server.registerTool(
    "jira_update_issue",
    {
      title: "Update an issue",
      description: "Change an issue's summary, description, priority or labels. Only the fields you pass are changed. Use jira_transition_issue to change status.",
      inputSchema: {
        ...keyShape,
        summary: z.string().optional(),
        description: z.string().optional().describe("Plain text; replaces the whole description"),
        priority: z.string().optional(),
        labels: z.array(z.string()).optional().describe("Replaces all labels"),
      },
    },
    async (a, extra) =>
      run(extra, async (jira) => {
        const fields = {
          ...(a.summary ? { summary: a.summary } : {}),
          ...(a.description !== undefined ? { description: toAdf(a.description) } : {}),
          ...(a.priority ? { priority: { name: a.priority } } : {}),
          ...(a.labels ? { labels: a.labels } : {}),
        };
        if (!Object.keys(fields).length) throw new Error("Nothing to update");
        await jira.request("PUT", `/rest/api/3/issue/${enc(a.key)}`, { body: { fields } });
        return { ok: true, url: `${jira.site}/browse/${a.key}` };
      }),
  );

  server.registerTool(
    "jira_list_comments",
    {
      title: "List comments",
      description: "List comments on an issue, oldest first.",
      inputSchema: { ...keyShape, maxResults: z.number().int().min(1).max(100).default(50) },
    },
    async (a, extra) =>
      run(extra, async (jira) => {
        const res = await jira.request<J>("GET", `/rest/api/3/issue/${enc(a.key)}/comment`, {
          query: { maxResults: a.maxResults, orderBy: "created" },
        });
        return { total: res.total, comments: (res.comments ?? []).map(slimComment) };
      }),
  );

  server.registerTool(
    "jira_add_comment",
    {
      title: "Add a comment",
      description: "Comment on an issue. Visible to others immediately; confirm with the user first.",
      inputSchema: { ...keyShape, body: z.string().describe("Plain text") },
    },
    async (a, extra) =>
      run(extra, async (jira) => {
        const c = await jira.request<J>("POST", `/rest/api/3/issue/${enc(a.key)}/comment`, { body: { body: toAdf(a.body) } });
        return slimComment(c);
      }),
  );

  server.registerTool(
    "jira_list_transitions",
    {
      title: "List transitions",
      description: "List the status transitions currently available for an issue (needed to get a transitionId).",
      inputSchema: keyShape,
    },
    async (a, extra) =>
      run(extra, async (jira) => {
        const res = await jira.request<J>("GET", `/rest/api/3/issue/${enc(a.key)}/transitions`);
        return (res.transitions ?? []).map((t: J) => ({ id: t.id, name: t.name, toStatus: t.to?.name }));
      }),
  );

  server.registerTool(
    "jira_transition_issue",
    {
      title: "Move an issue to a new status",
      description: "Apply a transition (e.g. To Do -> In Progress -> Done). Get the id from jira_list_transitions. Visible to others immediately.",
      inputSchema: {
        ...keyShape,
        transitionId: z.string(),
        comment: z.string().optional().describe("Optional comment to add with the transition"),
      },
    },
    async (a, extra) =>
      run(extra, async (jira) => {
        await jira.request("POST", `/rest/api/3/issue/${enc(a.key)}/transitions`, {
          body: {
            transition: { id: a.transitionId },
            ...(a.comment ? { update: { comment: [{ add: { body: toAdf(a.comment) } }] } } : {}),
          },
        });
        return { ok: true, url: `${jira.site}/browse/${a.key}` };
      }),
  );

  server.registerTool(
    "jira_assign_issue",
    {
      title: "Assign an issue",
      description: "Assign an issue to a user (accountId from jira_search_users), or pass null to unassign.",
      inputSchema: { ...keyShape, accountId: z.string().nullable() },
    },
    async (a, extra) =>
      run(extra, async (jira) => {
        await jira.request("PUT", `/rest/api/3/issue/${enc(a.key)}/assignee`, { body: { accountId: a.accountId } });
        return { ok: true };
      }),
  );

  server.registerTool(
    "jira_search_users",
    {
      title: "Search users",
      description: "Find users by name or email to get their accountId.",
      inputSchema: { query: z.string(), maxResults: z.number().int().min(1).max(50).default(10) },
    },
    async (a, extra) =>
      run(extra, async (jira) => {
        const res = await jira.request<J[]>("GET", "/rest/api/3/user/search", { query: { query: a.query, maxResults: a.maxResults } });
        return res.map(slimUser);
      }),
  );
}
