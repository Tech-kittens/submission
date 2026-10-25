import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { runGithub as run } from "@/lib/mcp";

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type J = Record<string, any>;

const MAX_TEXT = 50_000;
const MAX_DIFF = 100_000;

const trunc = (s: string | null | undefined, max = MAX_TEXT) =>
  s && s.length > max ? `${s.slice(0, max)}\n…[truncated]` : (s ?? undefined);

const enc = encodeURIComponent;
const encPath = (p: string) => p.split("/").map(enc).join("/");

const repoShape = {
  owner: z.string().describe("Repository owner (user or organization)"),
  repo: z.string().describe("Repository name"),
};
const rp = (a: { owner: string; repo: string }) => `/repos/${enc(a.owner)}/${enc(a.repo)}`;

const paging = {
  perPage: z.number().int().min(1).max(100).default(30),
  page: z.number().int().min(1).default(1),
};

const slimRepo = (r: J) => ({
  fullName: r.full_name,
  description: r.description,
  private: r.private,
  fork: r.fork,
  archived: r.archived,
  defaultBranch: r.default_branch,
  language: r.language,
  stars: r.stargazers_count,
  forks: r.forks_count,
  openIssues: r.open_issues_count,
  pushedAt: r.pushed_at,
  url: r.html_url,
});

const slimIssue = (i: J, withBody = false) => ({
  number: i.number,
  title: i.title,
  state: i.state,
  isPullRequest: Boolean(i.pull_request),
  author: i.user?.login,
  labels: (i.labels ?? []).map((l: J | string) => (typeof l === "string" ? l : l.name)),
  assignees: (i.assignees ?? []).map((a: J) => a.login),
  comments: i.comments,
  createdAt: i.created_at,
  updatedAt: i.updated_at,
  url: i.html_url,
  ...(withBody ? { body: trunc(i.body) } : {}),
});

const slimPr = (p: J, withBody = false) => ({
  number: p.number,
  title: p.title,
  state: p.state,
  draft: p.draft,
  merged: p.merged ?? Boolean(p.merged_at),
  mergeable: p.mergeable,
  author: p.user?.login,
  head: p.head?.ref,
  base: p.base?.ref,
  additions: p.additions,
  deletions: p.deletions,
  changedFiles: p.changed_files,
  commits: p.commits,
  createdAt: p.created_at,
  updatedAt: p.updated_at,
  url: p.html_url,
  ...(withBody ? { body: trunc(p.body) } : {}),
});

const slimCommit = (c: J) => ({
  sha: c.sha,
  message: c.commit?.message,
  author: c.author?.login ?? c.commit?.author?.name,
  date: c.commit?.author?.date,
  url: c.html_url,
});

const slimComment = (c: J) => ({
  id: c.id,
  author: c.user?.login,
  body: trunc(c.body, 10_000),
  createdAt: c.created_at,
  url: c.html_url,
});

export function registerGithubTools(server: McpServer) {
  // ───────────── account & repos ─────────────
  server.registerTool(
    "github_get_me",
    { title: "Who am I", description: "Get the connected GitHub user.", inputSchema: {} },
    async (_a, extra) =>
      run(extra, async (gh) => {
        const u = await gh.request<J>("GET", "/user");
        return { login: u.login, name: u.name, email: u.email, publicRepos: u.public_repos, url: u.html_url };
      }),
  );

  server.registerTool(
    "github_list_my_repos",
    {
      title: "List my repositories",
      description: "List repositories the connected user can access (owned, collaborator, org member), most recently pushed first.",
      inputSchema: {
        affiliation: z.string().default("owner,collaborator,organization_member").describe("Comma-separated: owner, collaborator, organization_member"),
        sort: z.enum(["created", "updated", "pushed", "full_name"]).default("pushed"),
        ...paging,
      },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J[]>("GET", "/user/repos", {
          query: { affiliation: a.affiliation, sort: a.sort, per_page: a.perPage, page: a.page },
        });
        return res.map(slimRepo);
      }),
  );

  server.registerTool(
    "github_search_repositories",
    {
      title: "Search repositories",
      description: "Search repositories with GitHub syntax, e.g. 'language:typescript stars:>1000 mcp' or 'user:octocat'.",
      inputSchema: { query: z.string(), ...paging },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J>("GET", "/search/repositories", {
          query: { q: a.query, per_page: a.perPage, page: a.page },
        });
        return { total: res.total_count, repositories: res.items.map(slimRepo) };
      }),
  );

  server.registerTool(
    "github_get_repository",
    { title: "Get a repository", description: "Get details about one repository.", inputSchema: repoShape },
    async (a, extra) => run(extra, async (gh) => slimRepo(await gh.request<J>("GET", rp(a)))),
  );

  server.registerTool(
    "github_get_file_contents",
    {
      title: "Read a file or directory",
      description: "Read a file (decoded text) or list a directory. Use an empty path for the repo root. Files over ~1MB aren't returned by the API.",
      inputSchema: { ...repoShape, path: z.string().default(""), ref: z.string().optional().describe("Branch, tag or commit SHA") },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J | J[]>("GET", `${rp(a)}/contents/${encPath(a.path)}`, { query: { ref: a.ref } });
        if (Array.isArray(res)) return res.map((e) => ({ name: e.name, path: e.path, type: e.type, size: e.size }));
        if (res.type !== "file") return { type: res.type, path: res.path, url: res.html_url };
        const content = res.encoding === "base64" ? Buffer.from(res.content ?? "", "base64").toString("utf8") : (res.content ?? "");
        return { path: res.path, sha: res.sha, size: res.size, content: trunc(content), url: res.html_url };
      }),
  );

  server.registerTool(
    "github_search_code",
    {
      title: "Search code",
      description: "Search code across GitHub, e.g. 'useEffect repo:vercel/next.js language:ts'. Scope with repo:, org: or user: qualifiers.",
      inputSchema: { query: z.string(), ...paging },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J>("GET", "/search/code", { query: { q: a.query, per_page: a.perPage, page: a.page } });
        return {
          total: res.total_count,
          results: res.items.map((i: J) => ({ repo: i.repository?.full_name, path: i.path, url: i.html_url })),
        };
      }),
  );

  server.registerTool(
    "github_list_branches",
    { title: "List branches", description: "List branches of a repository.", inputSchema: { ...repoShape, ...paging } },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J[]>("GET", `${rp(a)}/branches`, { query: { per_page: a.perPage, page: a.page } });
        return res.map((b) => ({ name: b.name, sha: b.commit?.sha, protected: b.protected }));
      }),
  );

  server.registerTool(
    "github_list_commits",
    {
      title: "List commits",
      description: "List commits, newest first. Optionally filter by branch/SHA, file path, author or date range.",
      inputSchema: {
        ...repoShape,
        sha: z.string().optional().describe("Branch name or commit SHA to start from"),
        path: z.string().optional(),
        author: z.string().optional().describe("GitHub login or email"),
        since: z.string().optional().describe("ISO 8601"),
        until: z.string().optional().describe("ISO 8601"),
        ...paging,
      },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J[]>("GET", `${rp(a)}/commits`, {
          query: { sha: a.sha, path: a.path, author: a.author, since: a.since, until: a.until, per_page: a.perPage, page: a.page },
        });
        return res.map(slimCommit);
      }),
  );

  server.registerTool(
    "github_create_branch",
    {
      title: "Create a branch",
      description: "Create a new branch from another branch (defaults to the repo's default branch).",
      inputSchema: { ...repoShape, branch: z.string().describe("New branch name"), fromBranch: z.string().optional() },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const from = a.fromBranch ?? (await gh.request<J>("GET", rp(a))).default_branch;
        const ref = await gh.request<J>("GET", `${rp(a)}/git/ref/heads/${encPath(from)}`);
        const created = await gh.request<J>("POST", `${rp(a)}/git/refs`, {
          body: { ref: `refs/heads/${a.branch}`, sha: ref.object.sha },
        });
        return { branch: a.branch, from, sha: created.object?.sha };
      }),
  );

  server.registerTool(
    "github_create_or_update_file",
    {
      title: "Create or update a file",
      description: "Commit a single file. To update an existing file you must pass its current blob sha (from github_get_file_contents). Creates a real commit; confirm with the user first.",
      inputSchema: {
        ...repoShape,
        path: z.string(),
        content: z.string().describe("Full new file content (plain text)"),
        message: z.string().describe("Commit message"),
        branch: z.string().optional().describe("Defaults to the default branch"),
        sha: z.string().optional().describe("Blob SHA of the file being replaced (required for updates)"),
      },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J>("PUT", `${rp(a)}/contents/${encPath(a.path)}`, {
          body: {
            message: a.message,
            content: Buffer.from(a.content, "utf8").toString("base64"),
            branch: a.branch,
            sha: a.sha,
          },
        });
        return { path: res.content?.path, fileSha: res.content?.sha, commit: res.commit?.sha, url: res.commit?.html_url };
      }),
  );

  // ───────────── issues ─────────────
  server.registerTool(
    "github_list_issues",
    {
      title: "List issues",
      description: "List issues in a repository (pull requests are excluded).",
      inputSchema: {
        ...repoShape,
        state: z.enum(["open", "closed", "all"]).default("open"),
        labels: z.string().optional().describe("Comma-separated label names"),
        assignee: z.string().optional().describe("Login, 'none' or '*'"),
        sort: z.enum(["created", "updated", "comments"]).default("updated"),
        ...paging,
      },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J[]>("GET", `${rp(a)}/issues`, {
          query: { state: a.state, labels: a.labels, assignee: a.assignee, sort: a.sort, per_page: a.perPage, page: a.page },
        });
        return res.filter((i) => !i.pull_request).map((i) => slimIssue(i));
      }),
  );

  server.registerTool(
    "github_search_issues",
    {
      title: "Search issues and PRs",
      description: "Search issues and pull requests across GitHub, e.g. 'is:open is:pr author:@me repo:owner/name' or 'is:issue assignee:@me state:open'.",
      inputSchema: { query: z.string(), ...paging },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J>("GET", "/search/issues", { query: { q: a.query, per_page: a.perPage, page: a.page } });
        return {
          total: res.total_count,
          items: res.items.map((i: J) => ({ ...slimIssue(i), repo: i.repository_url?.split("/repos/")[1] })),
        };
      }),
  );

  server.registerTool(
    "github_get_issue",
    {
      title: "Get an issue",
      description: "Get one issue with its body.",
      inputSchema: { ...repoShape, number: z.number().int() },
    },
    async (a, extra) => run(extra, async (gh) => slimIssue(await gh.request<J>("GET", `${rp(a)}/issues/${a.number}`), true)),
  );

  server.registerTool(
    "github_create_issue",
    {
      title: "Create an issue",
      description: "Open a new issue.",
      inputSchema: {
        ...repoShape,
        title: z.string(),
        body: z.string().optional(),
        labels: z.array(z.string()).optional(),
        assignees: z.array(z.string()).optional(),
      },
    },
    async (a, extra) =>
      run(extra, async (gh) =>
        slimIssue(
          await gh.request<J>("POST", `${rp(a)}/issues`, {
            body: { title: a.title, body: a.body, labels: a.labels, assignees: a.assignees },
          }),
          true,
        ),
      ),
  );

  server.registerTool(
    "github_update_issue",
    {
      title: "Update an issue or PR",
      description: "Change title, body, state, labels or assignees of an issue (or the metadata of a PR). Only the fields you pass are changed; labels/assignees replace the existing lists.",
      inputSchema: {
        ...repoShape,
        number: z.number().int(),
        title: z.string().optional(),
        body: z.string().optional(),
        state: z.enum(["open", "closed"]).optional(),
        stateReason: z.enum(["completed", "not_planned", "reopened"]).optional(),
        labels: z.array(z.string()).optional(),
        assignees: z.array(z.string()).optional(),
      },
    },
    async (a, extra) =>
      run(extra, async (gh) =>
        slimIssue(
          await gh.request<J>("PATCH", `${rp(a)}/issues/${a.number}`, {
            body: {
              title: a.title,
              body: a.body,
              state: a.state,
              state_reason: a.stateReason,
              labels: a.labels,
              assignees: a.assignees,
            },
          }),
        ),
      ),
  );

  server.registerTool(
    "github_list_comments",
    {
      title: "List comments",
      description: "List comments on an issue or pull request (conversation tab).",
      inputSchema: { ...repoShape, number: z.number().int(), ...paging },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J[]>("GET", `${rp(a)}/issues/${a.number}/comments`, {
          query: { per_page: a.perPage, page: a.page },
        });
        return res.map(slimComment);
      }),
  );

  server.registerTool(
    "github_add_comment",
    {
      title: "Comment on an issue or PR",
      description: "Post a comment on an issue or pull request. Visible to others immediately; confirm with the user first.",
      inputSchema: { ...repoShape, number: z.number().int(), body: z.string() },
    },
    async (a, extra) =>
      run(extra, async (gh) =>
        slimComment(await gh.request<J>("POST", `${rp(a)}/issues/${a.number}/comments`, { body: { body: a.body } })),
      ),
  );

  // ───────────── pull requests ─────────────
  server.registerTool(
    "github_list_pull_requests",
    {
      title: "List pull requests",
      description: "List pull requests in a repository.",
      inputSchema: {
        ...repoShape,
        state: z.enum(["open", "closed", "all"]).default("open"),
        head: z.string().optional().describe("Filter by head, as 'user:branch'"),
        base: z.string().optional().describe("Filter by base branch"),
        sort: z.enum(["created", "updated", "popularity", "long-running"]).default("updated"),
        ...paging,
      },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J[]>("GET", `${rp(a)}/pulls`, {
          query: { state: a.state, head: a.head, base: a.base, sort: a.sort, direction: "desc", per_page: a.perPage, page: a.page },
        });
        return res.map((p) => slimPr(p));
      }),
  );

  server.registerTool(
    "github_get_pull_request",
    {
      title: "Get a pull request",
      description: "Get one pull request with its description, mergeability and diff stats.",
      inputSchema: { ...repoShape, number: z.number().int() },
    },
    async (a, extra) => run(extra, async (gh) => slimPr(await gh.request<J>("GET", `${rp(a)}/pulls/${a.number}`), true)),
  );

  server.registerTool(
    "github_get_pull_request_diff",
    {
      title: "Get a PR diff",
      description: "Get the unified diff of a pull request (truncated if very large; use github_list_pull_request_files for per-file patches).",
      inputSchema: { ...repoShape, number: z.number().int() },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const diff = await gh.request<string>("GET", `${rp(a)}/pulls/${a.number}`, {
          accept: "application/vnd.github.diff",
          text: true,
        });
        return { diff: trunc(diff, MAX_DIFF) };
      }),
  );

  server.registerTool(
    "github_list_pull_request_files",
    {
      title: "List PR files",
      description: "List files changed in a pull request, with per-file patches.",
      inputSchema: { ...repoShape, number: z.number().int(), ...paging },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J[]>("GET", `${rp(a)}/pulls/${a.number}/files`, {
          query: { per_page: a.perPage, page: a.page },
        });
        return res.map((f) => ({
          filename: f.filename,
          status: f.status,
          additions: f.additions,
          deletions: f.deletions,
          patch: trunc(f.patch, 10_000),
        }));
      }),
  );

  server.registerTool(
    "github_create_pull_request",
    {
      title: "Open a pull request",
      description: "Open a pull request from a head branch into a base branch.",
      inputSchema: {
        ...repoShape,
        title: z.string(),
        head: z.string().describe("Branch with the changes (or 'user:branch' for forks)"),
        base: z.string().describe("Branch to merge into"),
        body: z.string().optional(),
        draft: z.boolean().default(false),
      },
    },
    async (a, extra) =>
      run(extra, async (gh) =>
        slimPr(
          await gh.request<J>("POST", `${rp(a)}/pulls`, {
            body: { title: a.title, head: a.head, base: a.base, body: a.body, draft: a.draft },
          }),
          true,
        ),
      ),
  );

  server.registerTool(
    "github_review_pull_request",
    {
      title: "Review a pull request",
      description: "Submit a review: APPROVE, REQUEST_CHANGES or COMMENT. Visible to others immediately; confirm with the user first.",
      inputSchema: {
        ...repoShape,
        number: z.number().int(),
        event: z.enum(["APPROVE", "REQUEST_CHANGES", "COMMENT"]),
        body: z.string().optional().describe("Required for REQUEST_CHANGES and COMMENT"),
      },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const r = await gh.request<J>("POST", `${rp(a)}/pulls/${a.number}/reviews`, {
          body: { event: a.event, body: a.body },
        });
        return { id: r.id, state: r.state, url: r.html_url };
      }),
  );

  server.registerTool(
    "github_merge_pull_request",
    {
      title: "Merge a pull request",
      description: "Merge a pull request. Irreversible; confirm with the user first.",
      inputSchema: {
        ...repoShape,
        number: z.number().int(),
        mergeMethod: z.enum(["merge", "squash", "rebase"]).default("merge"),
        commitTitle: z.string().optional(),
        commitMessage: z.string().optional(),
      },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const r = await gh.request<J>("PUT", `${rp(a)}/pulls/${a.number}/merge`, {
          body: { merge_method: a.mergeMethod, commit_title: a.commitTitle, commit_message: a.commitMessage },
        });
        return { merged: r.merged, sha: r.sha, message: r.message };
      }),
  );

  // ───────────── actions ─────────────
  server.registerTool(
    "github_list_workflow_runs",
    {
      title: "List workflow runs",
      description: "List recent GitHub Actions runs for a repository (CI status).",
      inputSchema: {
        ...repoShape,
        branch: z.string().optional(),
        status: z.string().optional().describe("e.g. completed, in_progress, queued, success, failure"),
        ...paging,
      },
    },
    async (a, extra) =>
      run(extra, async (gh) => {
        const res = await gh.request<J>("GET", `${rp(a)}/actions/runs`, {
          query: { branch: a.branch, status: a.status, per_page: a.perPage, page: a.page },
        });
        return (res.workflow_runs as J[]).map((r) => ({
          id: r.id,
          name: r.name,
          status: r.status,
          conclusion: r.conclusion,
          branch: r.head_branch,
          event: r.event,
          commit: r.head_sha?.slice(0, 7),
          createdAt: r.created_at,
          url: r.html_url,
        }));
      }),
  );
}
