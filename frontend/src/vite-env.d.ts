/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** "real" talks to the ASP.NET Core API; anything else uses the in-memory mock. */
  readonly VITE_API_MODE?: string;
  /** Optional API origin. Empty uses the same origin (Vite proxies /api to http://localhost:5000). */
  readonly VITE_API_BASE?: string;
}
interface ImportMeta {
  readonly env: ImportMetaEnv;
}
