import { useEffect, useState } from "react";
import { Sidebar } from "./components/Layout";
import { REAL, apiError, useStore } from "./store";
import { Dashboard } from "./pages/Dashboard";
import { NewCase } from "./pages/NewCase";
import { Cases } from "./pages/Cases";
import { CaseDetail } from "./pages/CaseDetail";
import { AuditLog, Help, Settings } from "./pages/Misc";

const read = () => window.location.hash.replace(/^#/, "") || "/";

export default function App() {
  const { theme } = useStore();
  const [route, setRoute] = useState(read());
  useEffect(() => {
    const on = () => setRoute(read());
    window.addEventListener("hashchange", on);
    return () => window.removeEventListener("hashchange", on);
  }, []);
  useEffect(() => { document.documentElement.dataset.theme = theme; }, [theme]);

  let page;
  if (route === "/") page = <Dashboard />;
  else if (route === "/new") page = <NewCase />;
  else if (route === "/cases") page = <Cases />;
  else if (route.startsWith("/cases/")) page = <CaseDetail key={route} id={route.slice(7)} />;
  else if (route === "/audit") page = <AuditLog />;
  else if (route === "/settings") page = <Settings />;
  else if (route === "/help") page = <Help />;
  else page = <Dashboard />;

  return (
    <div className="shell">
      <Sidebar route={route} />
      <main className="main">
        {REAL && apiError.message && <div className="note warn" role="alert" style={{ marginBottom: 12 }}>The API is not reachable: {apiError.message}</div>}
        {page}
      </main>
    </div>
  );
}
