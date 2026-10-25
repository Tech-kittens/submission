import { createMcpRoute } from "@/lib/mcp";
import { registerGmailTools } from "@/lib/tools/gmail";

export const runtime = "nodejs";
export const maxDuration = 60;

export const { GET, POST, DELETE } = createMcpRoute("gmail", registerGmailTools);
