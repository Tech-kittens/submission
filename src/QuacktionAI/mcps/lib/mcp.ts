import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { AuthInfo } from "@modelcontextprotocol/sdk/server/auth/types.js";
import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { createMcpHandler, withMcpAuth } from "mcp-handler";
import { getAccessToken, googleClient, type GoogleClient } from "@/lib/google";
import { githubClient, type GitHubClient } from "@/lib/github";
import { jiraClient, type JiraClient, type JiraCredential } from "@/lib/jira";
import { SERVICE_PATH, SERVICE_PROVIDER, SERVICE_SCOPES, type Provider, type Service } from "@/lib/scopes";
import { readToken } from "@/lib/token";

/** Build the GET/POST/DELETE exports for one service's MCP endpoint (<SERVICE_PATH>/mcp). */
export function createMcpRoute(service: Service, register: (server: McpServer) => void) {
  const handler = createMcpHandler(
    (server) => {
      register(server);
    },
    {},
    { basePath: SERVICE_PATH[service], maxDuration: 60, disableSse: true },
  );

  const verifyToken = async (_req: Request, bearer?: string): Promise<AuthInfo | undefined> => {
    if (!bearer) return undefined;
    const payload = readToken(bearer);
    if (!payload || payload.provider !== SERVICE_PROVIDER[service] || !payload.services.includes(service)) {
      return undefined;
    }
    // Jira bearers embed a ~1h Atlassian access token. Once it lapses, answer 401 so the client refreshes at /oauth/token.
    if (payload.provider === "jira") {
      const cred = parseJira(payload.credential);
      if (!cred || cred.exp < Date.now() + 30_000) return undefined;
    }
    return {
      token: bearer,
      clientId: payload.id,
      scopes: SERVICE_SCOPES[service],
      extra: { provider: payload.provider, credential: payload.credential, id: payload.id },
    };
  };

  // Point the 401's `resource_metadata` at THIS service's metadata document. The default is the origin-wide
  // /.well-known/oauth-protected-resource, which names no service, so the client would send an
  // unrecognisable `resource` to /oauth/authorize and we could not tell GitHub from Google.
  const authed = withMcpAuth(handler, verifyToken, {
    required: true,
    resourceMetadataPath: `/.well-known/oauth-protected-resource${SERVICE_PATH[service]}/mcp`,
  });
  return { GET: authed, POST: authed, DELETE: authed };
}

const parseJira = (credential: string): JiraCredential | null => {
  try {
    return JSON.parse(credential) as JiraCredential;
  } catch {
    return null;
  }
};

async function execute<C>(
  extra: { authInfo?: AuthInfo },
  provider: Provider,
  makeClient: (credential: string) => Promise<C>,
  fn: (client: C) => Promise<unknown>,
): Promise<CallToolResult> {
  try {
    const info = extra.authInfo?.extra;
    if (info?.provider !== provider || typeof info.credential !== "string") throw new Error("Not authenticated");
    const result = await fn(await makeClient(info.credential));
    return { content: [{ type: "text", text: JSON.stringify(result ?? { ok: true }, null, 2) }] };
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    return { isError: true, content: [{ type: "text", text: message }] };
  }
}

/** Run a Gmail/Calendar/Meet tool body with an authenticated Google client. */
export const run = (extra: { authInfo?: AuthInfo }, fn: (google: GoogleClient) => Promise<unknown>) =>
  execute(extra, "google", async (refreshToken) => googleClient(await getAccessToken(refreshToken)), fn);

/** Run a GitHub tool body with an authenticated GitHub client. */
export const runGithub = (extra: { authInfo?: AuthInfo }, fn: (github: GitHubClient) => Promise<unknown>) =>
  execute(extra, "github", async (token) => githubClient(token), fn);

/** Run a Jira tool body with an authenticated Jira client. */
export const runJira = (extra: { authInfo?: AuthInfo }, fn: (jira: JiraClient) => Promise<unknown>) =>
  execute(
    extra,
    "jira",
    async (credential) => {
      const cred = parseJira(credential);
      if (!cred) throw new Error("Not authenticated");
      return jiraClient(cred);
    },
    fn,
  );
