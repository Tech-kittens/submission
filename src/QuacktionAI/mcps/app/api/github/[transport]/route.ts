import { createMcpRoute } from "@/lib/mcp";
import { registerGithubTools } from "@/lib/tools/github";

export const runtime = "nodejs";
export const maxDuration = 60;

export const { GET, POST, DELETE } = createMcpRoute("github", registerGithubTools);
