import { SERVICES, SERVICE_LABELS, SERVICE_PATH, SERVICE_PROVIDER, servicesOf } from "@/lib/scopes";

const button = { font: "inherit", padding: ".4rem .9rem", cursor: "pointer" } as const;

export default function Home() {
  return (
    <main>
      <h1>Google + GitHub MCP</h1>
      <p>
        Gmail, Calendar, Meet and GitHub as MCP servers. In <b>Claude.ai</b>, add a custom connector with one of the URLs
        below and sign in when prompted. For <b>Claude Code</b> or other clients, connect below to get a bearer token.
      </p>
      <ul>
        {SERVICES.map((s) => (
          <li key={s}>
            {SERVICE_LABELS[s]}: <code>{SERVICE_PATH[s]}/mcp</code>
          </li>
        ))}
      </ul>

      <h2>Get a bearer token</h2>
      <form action="/api/auth/google" method="get">
        <h3>Google</h3>
        {servicesOf("google").map((s) => (
          <label key={s} style={{ display: "block" }}>
            <input type="checkbox" name="service" value={s} defaultChecked /> {SERVICE_LABELS[s]}
          </label>
        ))}
        <p>
          <button type="submit" style={button}>
            Connect Google account
          </button>
        </p>
      </form>
      <h3>GitHub</h3>
      <p>
        <a href="/api/auth/github">
          <button type="button" style={button}>
            Connect GitHub account
          </button>
        </a>
      </p>
      <p style={{ opacity: 0.7, fontSize: 14 }}>
        Jira has no bearer-token option (Atlassian rotates refresh tokens): add its URL as a connector in Claude.ai, or
        use <code>/mcp</code> in Claude Code.
      </p>
      <p style={{ opacity: 0.7, fontSize: 14 }}>
        Providers: {[...new Set(SERVICES.map((s) => SERVICE_PROVIDER[s]))].join(", ")}
      </p>
    </main>
  );
}
