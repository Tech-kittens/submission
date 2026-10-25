import { createMcpRoute } from "@/lib/mcp";
import { registerMeetTools } from "@/lib/tools/meet";

export const runtime = "nodejs";
export const maxDuration = 60;

export const { GET, POST, DELETE } = createMcpRoute("meet", registerMeetTools);
