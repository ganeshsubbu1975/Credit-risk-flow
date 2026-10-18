import { useRef, useState } from "react";
import { Topbar, go } from "../components/Layout";
import { Icon } from "../components/Icon";
import { PROFILES } from "../mock/data";
import { REAL, submitCase } from "../store";

const MAX_MB = 20;

export function NewCase() {
  const [file, setFile] = useState<File | null>(null);
  const [error, setError] = useState("");
  const [over, setOver] = useState(false);
  const [profile, setProfile] = useState("D");
  const input = useRef<HTMLInputElement>(null);

  const pick = (f: File | undefined) => {
    setError("");
    if (!f) return;
    if (f.type !== "application/pdf" && !f.name.toLowerCase().endsWith(".pdf")) return setError("Only PDF files are accepted.");
    if (f.size > MAX_MB * 1024 * 1024) return setError(`File is larger than ${MAX_MB} MB.`);
    setFile(f);
  };

  const start = async () => {
    if (!file) return;
    setError("");
    try {
      go("/cases/" + (await submitCase(file, profile)));
    } catch (e) {
      setError(e instanceof Error ? e.message : "Upload failed.");
    }
  };

  return (
    <>
      <Topbar title="New case" />
      <div className="two" style={{ gridTemplateColumns: "1.4fr 1fr" }}>
        <div className="card">
          <div className="card-head"><h3>Upload entity document</h3></div>
          <div
            className={"drop" + (over ? " over" : "")}
            onDragOver={(e) => { e.preventDefault(); setOver(true); }}
            onDragLeave={() => setOver(false)}
            onDrop={(e) => { e.preventDefault(); setOver(false); pick(e.dataTransfer.files[0]); }}
          >
            <Icon name="upload" size={34} />
            <h3>{file ? file.name : "Drop the entity PDF here"}</h3>
            <p style={{ color: "var(--muted)" }}>{file ? `${(file.size / 1024).toFixed(0)} KB` : `Digital (text-based) PDF only, up to ${MAX_MB} MB`}</p>
            <div style={{ marginTop: 16 }}>
              <button className="btn ghost" onClick={() => input.current?.click()}>Browse files</button>
              <input ref={input} type="file" accept="application/pdf,.pdf" hidden onChange={(e) => pick(e.target.files?.[0])} />
            </div>
            {error && <div className="err" role="alert">{error}</div>}
          </div>
          <div style={{ marginTop: 16, display: "flex", justifyContent: "flex-end" }}>
            <button className="btn primary" disabled={!file} onClick={start}>Start analysis</button>
          </div>
        </div>

        <div className="card">
          <div className="card-head"><h3>What happens next</h3><span className="badge-mode">{REAL ? "Live API" : "Mock mode"}</span></div>
          <ol style={{ margin: 0, paddingLeft: 18, lineHeight: 1.9, color: "var(--muted)" }}>
            <li>The PDF is read and loaded into the vector database for this case.</li>
            <li>Node 1 extracts the financial metrics.</li>
            <li>Node 2 checks fraud and compliance.</li>
            <li>Node 3 writes the risk synthesis.</li>
            <li>The scorecard and decision model route the case.</li>
          </ol>
          {!REAL && (
            <div className="note warn" style={{ marginTop: 16 }}>
              Demo only: the backend is not connected, so the PDF is not parsed. Pick which benchmark entity the mock extraction should return.
              <select className="field" style={{ marginTop: 10 }} value={profile} onChange={(e) => setProfile(e.target.value)} aria-label="Mock extraction profile">
                {PROFILES.map((p) => <option key={p.key} value={p.key}>Entity {p.key}: {p.entity}</option>)}
              </select>
            </div>
          )}
        </div>
      </div>
    </>
  );
}
