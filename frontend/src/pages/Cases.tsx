import { useStore } from "../store";
import { Topbar, go } from "../components/Layout";
import { EntityCell, StatusPill, fmtDate } from "../components/bits";

export function Cases() {
  const { cases, search } = useStore();
  const q = search.trim().toLowerCase();
  const rows = q ? cases.filter((c) => [c.id, c.entity, c.fileName, c.status].some((v) => v.toLowerCase().includes(q))) : cases;
  return (
    <>
      <Topbar title="Cases" />
      <div className="card">
        <table>
          <thead><tr><th>Case</th><th>Entity</th><th>File</th><th>Submitted</th><th>By</th><th>Risk score</th><th>Status</th></tr></thead>
          <tbody>
            {rows.map((c) => (
              <tr key={c.id} className="click" onClick={() => go("/cases/" + c.id)}>
                <td>{c.id}</td>
                <td><EntityCell name={c.entity} /></td>
                <td>{c.fileName}</td>
                <td>{fmtDate(c.submittedAt)}</td>
                <td>{c.submittedBy}</td>
                <td>{c.riskScore ?? "..."}</td>
                <td><StatusPill s={c.status} /></td>
              </tr>
            ))}
          </tbody>
        </table>
        {!rows.length && <div className="empty">No cases match your search.</div>}
      </div>
    </>
  );
}
