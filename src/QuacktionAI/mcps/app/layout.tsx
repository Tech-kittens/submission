import type { ReactNode } from "react";

export const metadata = { title: "Google + GitHub MCP", description: "Gmail, Calendar, Meet and GitHub as MCP servers" };

export default function RootLayout({ children }: { children: ReactNode }) {
  return (
    <html lang="en">
      <body
        style={{
          font: "16px/1.5 system-ui, sans-serif",
          maxWidth: "40rem",
          margin: "3rem auto",
          padding: "0 1rem",
        }}
      >
        {children}
      </body>
    </html>
  );
}
