import { createMcpRoute } from "@/lib/mcp";
import { registerCalendarTools } from "@/lib/tools/calendar";

export const runtime = "nodejs";
export const maxDuration = 60;

export const { GET, POST, DELETE } = createMcpRoute("calendar", registerCalendarTools);
