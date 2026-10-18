import { useState } from "react";

interface Pt { label: string; approved: number; other: number }

// Smooth two-series line chart with a hover tooltip, echoing the reference dashboard.
export function LineChart({ data }: { data: Pt[] }) {
  const W = 640, H = 230, L = 38, R = 16, T = 18, B = 26;
  const max = Math.max(...data.flatMap((d) => [d.approved, d.other])) * 1.15;
  const x = (i: number) => L + (i * (W - L - R)) / (data.length - 1);
  const y = (v: number) => T + (1 - v / max) * (H - T - B);
  const path = (k: "approved" | "other") => {
    let d = `M ${x(0)} ${y(data[0][k])}`;
    for (let i = 1; i < data.length; i++) {
      const cx = (x(i - 1) + x(i)) / 2;
      d += ` C ${cx} ${y(data[i - 1][k])}, ${cx} ${y(data[i][k])}, ${x(i)} ${y(data[i][k])}`;
    }
    return d;
  };
  const [hover, setHover] = useState<number | null>(null);
  const ticks = [0, 0.25, 0.5, 0.75, 1].map((t) => Math.round(max * t));
  return (
    <svg viewBox={`0 0 ${W} ${H}`} width="100%" role="img" aria-label="Cases by outcome"
      onMouseLeave={() => setHover(null)}
      onMouseMove={(e) => {
        const r = (e.currentTarget as SVGSVGElement).getBoundingClientRect();
        const px = ((e.clientX - r.left) / r.width) * W;
        setHover(Math.max(0, Math.min(data.length - 1, Math.round(((px - L) / (W - L - R)) * (data.length - 1)))));
      }}>
      {ticks.map((t) => (
        <g key={t}>
          <line x1={L} x2={W - R} y1={y(t)} y2={y(t)} stroke="var(--line)" strokeDasharray="3 4" />
          <text x={L - 8} y={y(t) + 4} fontSize="10" textAnchor="end" fill="var(--muted)">{t}</text>
        </g>
      ))}
      {data.map((d, i) => <text key={d.label} x={x(i)} y={H - 6} fontSize="10" textAnchor="middle" fill="var(--muted)">{d.label}</text>)}
      <path d={path("approved")} fill="none" stroke="var(--green)" strokeWidth="2.5" />
      <path d={path("other")} fill="none" stroke="var(--gold)" strokeWidth="2.5" />
      {hover !== null && (
        <g>
          <line x1={x(hover)} x2={x(hover)} y1={T} y2={H - B} stroke="var(--purple)" strokeDasharray="3 3" />
          <circle cx={x(hover)} cy={y(data[hover].approved)} r="4.5" fill="var(--panel)" stroke="var(--green)" strokeWidth="2" />
          <circle cx={x(hover)} cy={y(data[hover].other)} r="4.5" fill="var(--panel)" stroke="var(--gold)" strokeWidth="2" />
          <g transform={`translate(${Math.min(x(hover) + 10, W - 130)}, ${T + 6})`}>
            <rect width="118" height="52" rx="8" fill="var(--panel)" stroke="var(--line)" />
            <circle cx="12" cy="18" r="3.5" fill="var(--green)" /><text x="22" y="22" fontSize="11" fill="var(--muted)">Approved</text>
            <text x="108" y="22" fontSize="11" textAnchor="end" fontWeight="600" fill="var(--text)">{data[hover].approved}</text>
            <circle cx="12" cy="38" r="3.5" fill="var(--gold)" /><text x="22" y="42" fontSize="11" fill="var(--muted)">Review/Deny</text>
            <text x="108" y="42" fontSize="11" textAnchor="end" fontWeight="600" fill="var(--text)">{data[hover].other}</text>
          </g>
        </g>
      )}
    </svg>
  );
}

// Semicircle gauge with three arcs and a centre total.
export function Gauge({ parts, total, caption }: { parts: { value: number; color: string }[]; total: number; caption: string }) {
  const cx = 150, cy = 140, r = 110, w = 34;
  const sum = parts.reduce((s, p) => s + p.value, 0) || 1;
  const gap = 0.03;
  let a = Math.PI;
  const arcs = parts.map((p) => {
    const span = (p.value / sum) * Math.PI;
    const a0 = a + gap / 2, a1 = a + span - gap / 2;
    a += span;
    const pt = (ang: number, rad: number) => `${cx + rad * Math.cos(ang)} ${cy + rad * Math.sin(ang)}`;
    const big = a1 - a0 > Math.PI ? 1 : 0;
    return { d: `M ${pt(a0, r)} A ${r} ${r} 0 ${big} 1 ${pt(a1, r)} L ${pt(a1, r - w)} A ${r - w} ${r - w} 0 ${big} 0 ${pt(a0, r - w)} Z`, color: p.color };
  });
  return (
    <div className="gauge-wrap">
      <svg viewBox="0 0 300 170" width="100%" role="img" aria-label={caption}>
        {arcs.map((x, i) => <path key={i} d={x.d} fill={x.color} />)}
        <text x={cx} y={cy - 10} textAnchor="middle" fontSize="26" fontWeight="600" fill="var(--text)">{total}</text>
        <text x={cx} y={cy + 12} textAnchor="middle" fontSize="11" fill="var(--muted)">{caption}</text>
      </svg>
    </div>
  );
}
