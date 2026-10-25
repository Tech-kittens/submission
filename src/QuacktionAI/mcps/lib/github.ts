export class GitHubApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}

const API = "https://api.github.com";

type QueryValue = string | number | boolean | undefined;

export interface GitHubClient {
  request<T = unknown>(
    method: "GET" | "POST" | "PUT" | "PATCH" | "DELETE",
    path: string,
    options?: { query?: Record<string, QueryValue>; body?: unknown; accept?: string; text?: boolean },
  ): Promise<T>;
}

export function githubClient(token: string): GitHubClient {
  return {
    async request<T>(
      method: string,
      path: string,
      options: { query?: Record<string, QueryValue>; body?: unknown; accept?: string; text?: boolean } = {},
    ): Promise<T> {
      const url = new URL(path.startsWith("http") ? path : `${API}${path}`);
      for (const [key, value] of Object.entries(options.query ?? {})) {
        if (value !== undefined) url.searchParams.set(key, String(value));
      }
      const hasBody = options.body !== undefined;
      const res = await fetch(url, {
        method,
        headers: {
          authorization: `Bearer ${token}`,
          accept: options.accept ?? "application/vnd.github+json",
          "x-github-api-version": "2022-11-28",
          "user-agent": "github-mcp",
          ...(hasBody ? { "content-type": "application/json" } : {}),
        },
        body: hasBody ? JSON.stringify(options.body) : undefined,
      });
      const text = await res.text();
      if (!res.ok) {
        let message = text || res.statusText;
        try {
          message = (JSON.parse(text) as { message?: string }).message ?? message;
        } catch {
          /* not JSON */
        }
        throw new GitHubApiError(res.status, `GitHub API ${res.status}: ${message}`);
      }
      if (options.text) return text as T;
      return (text ? JSON.parse(text) : undefined) as T;
    },
  };
}
