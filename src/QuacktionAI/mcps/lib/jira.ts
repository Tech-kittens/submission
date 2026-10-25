import { env } from "@/lib/env";

/**
 * Atlassian (Jira Cloud) OAuth 2.0 "3LO".
 *
 * Unlike Google/GitHub, Atlassian ROTATES refresh tokens: every refresh returns a new refresh token and the old
 * one dies shortly after. This app is stateless (no database), so we can't just embed a refresh token in a
 * long-lived bearer. Instead Jira connections use short-lived bearers (the Atlassian access token, ~1h) plus a
 * sealed refresh token handed to the MCP client, which presents it at /oauth/token to get the next pair.
 */

export class JiraApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}

const TOKEN_URL = "https://auth.atlassian.com/oauth/token";
const API = "https://api.atlassian.com";

/** What we embed in the bearer for each MCP call. */
export interface JiraCredential {
  at: string; // Atlassian access token
  exp: number; // epoch ms
  cloudId: string;
  site: string; // https://your-team.atlassian.net
}

export interface JiraTokens {
  accessToken: string;
  refreshToken: string;
  exp: number;
}

async function tokenRequest(body: Record<string, string>): Promise<JiraTokens> {
  const res = await fetch(TOKEN_URL, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      client_id: env("ATLASSIAN_CLIENT_ID"),
      client_secret: env("ATLASSIAN_CLIENT_SECRET"),
      ...body,
    }),
  });
  const json = (await res.json().catch(() => ({}))) as {
    access_token?: string;
    refresh_token?: string;
    expires_in?: number;
    error?: string;
    error_description?: string;
  };
  if (!res.ok || !json.access_token) {
    throw new Error(json.error_description ?? json.error ?? "Atlassian token request failed");
  }
  if (!json.refresh_token) {
    throw new Error("Atlassian returned no refresh token. Make sure the offline_access scope is enabled.");
  }
  return {
    accessToken: json.access_token,
    refreshToken: json.refresh_token,
    exp: Date.now() + (json.expires_in ?? 3600) * 1000,
  };
}

export const exchangeCode = (code: string, redirectUri: string) =>
  tokenRequest({ grant_type: "authorization_code", code, redirect_uri: redirectUri });

export const refreshTokens = (refreshToken: string) =>
  tokenRequest({ grant_type: "refresh_token", refresh_token: refreshToken });

/** The first Jira site this account can reach. (Multi-site accounts get the first one.) */
export async function firstSite(accessToken: string): Promise<{ cloudId: string; site: string }> {
  const res = await fetch(`${API}/oauth/token/accessible-resources`, {
    headers: { authorization: `Bearer ${accessToken}`, accept: "application/json" },
  });
  const sites = (await res.json().catch(() => [])) as { id: string; url: string; scopes?: string[] }[];
  if (!res.ok || !Array.isArray(sites) || !sites.length) {
    throw new Error("No Jira site is accessible with this account.");
  }
  const jira = sites.find((s) => s.scopes?.some((sc) => /:jira-/.test(sc))) ?? sites[0];
  return { cloudId: jira.id, site: jira.url.replace(/\/$/, "") };
}

type QueryValue = string | number | boolean | undefined;

export interface JiraClient {
  site: string;
  request<T = unknown>(
    method: "GET" | "POST" | "PUT" | "DELETE",
    path: string, // e.g. /rest/api/3/issue/ABC-1
    options?: { query?: Record<string, QueryValue>; body?: unknown },
  ): Promise<T>;
}

export function jiraClient(cred: JiraCredential): JiraClient {
  return {
    site: cred.site,
    async request<T>(
      method: string,
      path: string,
      options: { query?: Record<string, QueryValue>; body?: unknown } = {},
    ): Promise<T> {
      const url = new URL(`${API}/ex/jira/${cred.cloudId}${path}`);
      for (const [key, value] of Object.entries(options.query ?? {})) {
        if (value !== undefined) url.searchParams.set(key, String(value));
      }
      const hasBody = options.body !== undefined;
      const res = await fetch(url, {
        method,
        headers: {
          authorization: `Bearer ${cred.at}`,
          accept: "application/json",
          ...(hasBody ? { "content-type": "application/json" } : {}),
        },
        body: hasBody ? JSON.stringify(options.body) : undefined,
      });
      const text = await res.text();
      if (!res.ok) {
        let message = text || res.statusText;
        try {
          const j = JSON.parse(text) as { errorMessages?: string[]; errors?: Record<string, string>; message?: string };
          const parts = [...(j.errorMessages ?? []), ...Object.entries(j.errors ?? {}).map(([k, v]) => `${k}: ${v}`)];
          message = parts.length ? parts.join("; ") : (j.message ?? message);
        } catch {
          /* not JSON */
        }
        throw new JiraApiError(res.status, `Jira API ${res.status}: ${message}`);
      }
      return (text ? JSON.parse(text) : undefined) as T;
    },
  };
}
