import { open, seal } from "@/lib/crypto";
import type { Provider, Service } from "@/lib/scopes";

const PREFIX = "mcp_";
const LEGACY_GOOGLE_PREFIX = "gmcp_"; // tokens minted by the old Google-only app

export interface TokenPayload {
  provider: Provider;
  credential: string; // Google refresh token, or GitHub access token
  id: string; // email (Google) or login (GitHub)
  services: Service[];
  iat: number;
}

/** The bearer token MCP clients send. Stateless: no database. */
export function mintToken(payload: Omit<TokenPayload, "iat">): string {
  return PREFIX + seal({ ...payload, iat: Date.now() });
}

export function readToken(bearer: string): TokenPayload | null {
  if (bearer.startsWith(PREFIX)) return open<TokenPayload>(bearer.slice(PREFIX.length));
  if (bearer.startsWith(LEGACY_GOOGLE_PREFIX)) {
    const old = open<{ rt: string; email: string; services: Service[]; iat: number }>(
      bearer.slice(LEGACY_GOOGLE_PREFIX.length),
    );
    return old && { provider: "google", credential: old.rt, id: old.email, services: old.services, iat: old.iat };
  }
  return null;
}

/** Jira only: the sealed refresh token we give the MCP client (Atlassian rotates the real one on every use). */
export interface JiraRefresh {
  k: "jira-refresh";
  rt: string; // Atlassian refresh token
  cloudId: string;
  site: string;
  id: string;
  services: Service[];
}

export const sealRefresh = (r: Omit<JiraRefresh, "k">) => seal({ k: "jira-refresh", ...r });

export function readRefresh(sealed: string): JiraRefresh | null {
  const r = open<JiraRefresh>(sealed);
  return r?.k === "jira-refresh" ? r : null;
}
