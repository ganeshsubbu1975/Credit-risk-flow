import type { ReactNode } from "react";
import { Icon } from "./Icon";
import { setRole, setSearch, toggleTheme, useStore } from "../store";
import type { Role } from "../types";

export function go(path: string) { window.location.hash = path; }

const NAV = [
  { path: "/", label: "Dashboard", icon: "home" },
  { path: "/new", label: "New case", icon: "upload" },
  { path: "/cases", label: "Cases", icon: "cases" },
  { path: "/audit", label: "Audit log", icon: "audit" },
];

export function Sidebar({ route }: { route: string }) {
  const active = (p: string) => (p === "/" ? route === "/" : route.startsWith(p));
  return (
    <aside className="sidebar">
      <div className="brand"><span className="brand-mark"><Icon name="shield" size={18} /></span>LoanFlow</div>
      <div className="nav-label">Menu</div>
      {NAV.map((n) => (
        <button key={n.path} className={"nav-item" + (active(n.path) ? " active" : "")} onClick={() => go(n.path)}>
          <Icon name={n.icon} />{n.label}
        </button>
      ))}
      <div className="nav-gap" />
      <div className="nav-label">General</div>
      <button className="nav-item" onClick={() => go("/settings")}><Icon name="settings" />Settings</button>
      <button className="nav-item" onClick={() => go("/help")}><Icon name="help" />Help center</button>
      <div className="side-card">
        <span className="bolt"><Icon name="bolt" size={15} /></span>
        <h4>Decision model DM-1.0.0</h4>
        <p>Approve under 30, deny over 70, human review in between. The AI advises; the rules decide.</p>
      </div>
      <button className="logout"><Icon name="logout" />Log out</button>
    </aside>
  );
}

export function Topbar({ title }: { title: ReactNode }) {
  const { user, theme, search } = useStore();
  return (
    <div className="topbar">
      <h1>{title}</h1>
      <div className="tools">
        <label className="search">
          <Icon name="search" size={16} />
          <input placeholder="Search cases" value={search} onChange={(e) => { setSearch(e.target.value); go("/cases"); }} />
          <span className="kbd">Ctrl K</span>
        </label>
        <select className="role-select" value={user.role} onChange={(e) => setRole(e.target.value as Role)} aria-label="Demo role">
          <option value="RiskOps">Risk Ops</option>
          <option value="Underwriter">Underwriter</option>
          <option value="Auditor">Auditor</option>
        </select>
        <button className="icon-btn alert" aria-label="Notifications"><Icon name="bell" /></button>
        <div className="toggle" role="group" aria-label="Theme">
          <button className={theme === "light" ? "on" : ""} onClick={() => theme !== "light" && toggleTheme()} aria-label="Light"><Icon name="sun" size={15} /></button>
          <button className={theme === "dark" ? "on" : ""} onClick={() => theme !== "dark" && toggleTheme()} aria-label="Dark"><Icon name="moon" size={15} /></button>
        </div>
        <div className="avatar" title={user.name}>{user.name.split(" ").map((p) => p[0]).join("")}</div>
      </div>
    </div>
  );
}
