import { createMcpRoute } from "@/lib/mcp";
import { registerJiraTools } from "@/lib/tools/jira";

export const runtime = "nodejs";
export const maxDuration = 60;

export const { GET, POST, DELETE } = createMcpRoute("jira", registerJiraTools);
