import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  /**
   * A folder of files, not a Node server. The shipped product is one exe: the
   * host process serves `out/` at its own root (see HostSurface.ClientPath),
   * so the client and the API share an origin and there is no second runtime
   * to install, supervise or explain to Steam.
   *
   * What this costs is every Next feature that needs a server at request
   * time — rewrites, redirects, headers, middleware, Server Actions, ISR,
   * the default image loader. None of them is used: this is a client-side
   * SPA that talks to the host over fetch and SSE.
   */
  output: "export",

  /**
   * `/overlay/` rather than `/overlay`, so the export writes
   * out/overlay/index.html instead of out/overlay.html. ASP.NET's static file
   * middleware has no nginx-style `try_files $uri.html`; it does serve a
   * directory's default document, which is exactly the shape this produces.
   */
  trailingSlash: true,
};

export default nextConfig;
