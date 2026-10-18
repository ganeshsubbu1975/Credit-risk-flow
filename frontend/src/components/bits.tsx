import type { CSSProperties } from "react";
import type { CaseStatus } from "../types";

const COLORS = ["#6d4aff", "#ff9f43", "#4cc9f0", "#1fcf8a", "#c99700", "#ef4b4b"];
export const avatarColor = (s: string) => COLORS[[...s].reduce((a, c) => a + c.charCodeAt(0), 0) % COLORS.length];

export function EntityCell({ name }: { name: string }) {
  return (
    <div className="who">
      <span className="dot-av" style={{ background: avatarColor(name) }}>{name.split(" ").map((p) => p[0]).slice(0, 2).join("")}</span>
      {name}
    </div>
  );
}

const STATUS: Record<CaseStatus, { cls: string; label: string }> = {
  Processing: { cls: "cyan", label: "Processing" },
  AwaitingReview: { cls: "gold", label: "Awaiting review" },
  Approved: { cls: "green", label: "Approved" },
  Denied: { cls: "red", label: "Denied" },
  Error: { cls: "red", label: "Error" },
};
export const StatusPill = ({ s }: { s: CaseStatus }) => <span className={"pill " + STATUS[s].cls}>{STATUS[s].label}</span>;

export function Kpi({ title, value, chip, foot, color }: { title: string; value: string | number; chip?: string; foot: string; color: string }) {
  return (
    <div className="kpi" style={{ "--c": color } as CSSProperties}>
      <div className="kpi-head">{title}<span className="dots">...</span></div>
      <div className="kpi-value">{value}{chip && <span className="chip">{chip}</span>}</div>
      <div className="kpi-foot">{foot}</div>
    </div>
  );
}

export const fmtDate = (iso: string) => new Date(iso).toLocaleString("en-GB", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit" });
