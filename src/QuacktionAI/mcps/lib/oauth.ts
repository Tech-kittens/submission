import { createHash } from "node:crypto";
import { SERVICE_PATH, SERVICES, type Provider, type Service } from "@/lib/scopes";
import type { JiraRefresh, TokenPayload } from "@/lib/token";

export interface ClientInfo {
  redirect_uris: string[];
  client_name?: string;
}

/** What /oauth/authorize remembers (in the sealed cookie) while the user is at Google/GitHub. */
export interface McpAuthRequest {
  client_id: string;
  redirect_uri: string;
  state?: string;
  code_challenge: string;
}

/** Contents of the sealed sign-in cookie. */
export interface SavedAuth {
  state: string;
  services: Service[];
  mcp?: McpAuthRequest;
}

/** Contents of the sealed authorization code handed back to the MCP client.
 *  The bearer is minted at /oauth/token, so the (large) provider credential only travels once. */
export interface AuthCode extends McpAuthRequest {
  payload: Omit<TokenPayload, "iat">;
  refresh?: Omit<JiraRefresh, "k">; // Jira only
  exp: number;
}

export const cookieName = (provider: Provider) => `mcp_oauth_${provider}`;
export const cookiePath = (provider: Provider) => `/api/auth/${provider}`;

/** Only let Claude (and local dev clients) register. Open registration + any redirect_uri would let
 *  an attacker phish a victim's token, so keep this list tight. */
const HOSTS = ["claude.ai", "claude.com", "quackback.ofneill.com"];
export function redirectAllowed(uri: string): boolean {
  try {
    const u = new URL(uri);
    if (u.protocol === "https:") return HOSTS.includes(u.hostname);
    return u.protocol === "http:" && ["localhost", "127.0.0.1", "[::1]"].includes(u.hostname);
  } catch {
    return false;
  }
}

export const pkceMatches = (verifier: string, challenge: string) =>
  createHash("sha256").update(verifier).digest("base64url") === challenge;

/** The `resource` param is the MCP URL, e.g. https://host/api/github/mcp -> "github".
 *  Matches any URL at or under a service's path, so adding a service to SERVICE_PATH is all it takes. */
export function serviceFromResource(resource: string | null): Service | null {
  if (!resource) return null;
  let path: string;
  try {
    path = new URL(resource).pathname.replace(/\/+$/, "") + "/";
  } catch {
    return null;
  }
  const hits = SERVICES.filter((s) => path.startsWith(`${SERVICE_PATH[s]}/`));
  // Longest prefix wins (e.g. /api/google/gmail over a hypothetical /api/google).
  return hits.sort((a, b) => SERVICE_PATH[b].length - SERVICE_PATH[a].length)[0] ?? null;
}

export const noStore = { "cache-control": "no-store" };
