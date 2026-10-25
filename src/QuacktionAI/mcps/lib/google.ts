import { env } from "@/lib/env";

export class GoogleApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}

const accessTokens = new Map<string, { token: string; expiresAt: number }>();

export async function getAccessToken(refreshToken: string): Promise<string> {
  const cached = accessTokens.get(refreshToken);
  if (cached && cached.expiresAt > Date.now() + 60_000) return cached.token;

  const res = await fetch("https://oauth2.googleapis.com/token", {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      client_id: env("GOOGLE_CLIENT_ID"),
      client_secret: env("GOOGLE_CLIENT_SECRET"),
      refresh_token: refreshToken,
      grant_type: "refresh_token",
    }),
  });
  const json = (await res.json()) as {
    access_token?: string;
    expires_in?: number;
    error_description?: string;
  };
  if (!res.ok || !json.access_token) {
    throw new GoogleApiError(
      res.status,
      json.error_description ?? "Could not refresh the Google access token. Reconnect your account.",
    );
  }
  accessTokens.set(refreshToken, {
    token: json.access_token,
    expiresAt: Date.now() + (json.expires_in ?? 3600) * 1000,
  });
  return json.access_token;
}

type QueryValue = string | number | boolean | string[] | undefined;

export interface GoogleClient {
  request<T = unknown>(
    method: "GET" | "POST" | "PUT" | "PATCH" | "DELETE",
    url: string,
    options?: { query?: Record<string, QueryValue>; body?: unknown },
  ): Promise<T>;
}

export function googleClient(accessToken: string): GoogleClient {
  return {
    async request<T>(
      method: string,
      url: string,
      options: { query?: Record<string, QueryValue>; body?: unknown } = {},
    ): Promise<T> {
      const target = new URL(url);
      for (const [key, value] of Object.entries(options.query ?? {})) {
        if (value === undefined) continue;
        for (const item of Array.isArray(value) ? value : [value]) {
          target.searchParams.append(key, String(item));
        }
      }
      const hasBody = options.body !== undefined;
      const res = await fetch(target, {
        method,
        headers: {
          authorization: `Bearer ${accessToken}`,
          ...(hasBody ? { "content-type": "application/json" } : {}),
        },
        body: hasBody ? JSON.stringify(options.body) : undefined,
      });
      const text = await res.text();
      let data: unknown;
      try {
        data = text ? JSON.parse(text) : undefined;
      } catch {
        data = undefined;
      }
      if (!res.ok) {
        const message =
          (data as { error?: { message?: string } } | undefined)?.error?.message ?? (text || res.statusText);
        throw new GoogleApiError(res.status, `Google API ${res.status}: ${message}`);
      }
      return data as T;
    },
  };
}
