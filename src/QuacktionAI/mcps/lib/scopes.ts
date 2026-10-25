export const SERVICES = ["gmail", "calendar", "meet", "github", "jira"] as const;
export type Service = (typeof SERVICES)[number];

export const PROVIDERS = ["google", "github", "jira"] as const;
export type Provider = (typeof PROVIDERS)[number];
export const isProvider = (v: string | null): v is Provider => !!v && (PROVIDERS as readonly string[]).includes(v);

export const SERVICE_PROVIDER: Record<Service, Provider> = {
  gmail: "google",
  calendar: "google",
  meet: "google",
  github: "github",
  jira: "jira",
};

export const SERVICE_LABELS: Record<Service, string> = {
  gmail: "Gmail",
  calendar: "Google Calendar",
  meet: "Google Meet",
  github: "GitHub",
  jira: "Jira",
};

/** Where each service's MCP endpoint lives (must match the folders under app/api). */
export const SERVICE_PATH: Record<Service, string> = {
  gmail: "/api/google/gmail",
  calendar: "/api/google/calendar",
  meet: "/api/google/meet",
  github: "/api/github",
  jira: "/api/jira",
};

export const PROVIDER_LABELS: Record<Provider, string> = { google: "Google", github: "GitHub", jira: "Jira" };

export const SERVICE_SCOPES: Record<Service, string[]> = {
  gmail: ["https://www.googleapis.com/auth/gmail.modify"],
  calendar: [
    "https://www.googleapis.com/auth/calendar.events",
    "https://www.googleapis.com/auth/calendar.readonly",
  ],
  meet: [
    "https://www.googleapis.com/auth/meetings.space.created",
    "https://www.googleapis.com/auth/meetings.space.readonly",
  ],
  // repo = read/write repos, issues, PRs, Actions runs. read:user = your profile.
  github: ["repo", "read:user"],
  // offline_access = refresh token. Atlassian rotates refresh tokens, see lib/jira.ts.
  jira: ["read:jira-work", "write:jira-work", "read:jira-user", "offline_access"],
};

export const isService = (value: string): value is Service =>
  (SERVICES as readonly string[]).includes(value);

export const servicesOf = (provider: Provider): Service[] =>
  SERVICES.filter((s) => SERVICE_PROVIDER[s] === provider);
