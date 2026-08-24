"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import {
  createJob,
  listJobs,
  subscribeEvents,
  TERMINAL,
  type Job,
  type JobEventDto,
} from "@/lib/relay";

export default function Dashboard() {
  const router = useRouter();
  const [jobs, setJobs] = useState<Job[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [repo, setRepo] = useState("");
  const [prompt, setPrompt] = useState("");
  const [maxTokens, setMaxTokens] = useState("120000");
  const [submitting, setSubmitting] = useState(false);

  async function refresh() {
    try {
      setJobs(await listJobs());
      setError(null);
    } catch (e) {
      setError(`relay server unreachable (${String(e)})`);
    }
  }

  useEffect(() => {
    refresh();
    const t = setInterval(refresh, 3000);
    return () => clearInterval(t);
  }, []);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setSubmitting(true);
    try {
      const job = await createJob({
        repo_url: repo,
        prompt,
        budget: maxTokens ? { max_tokens: Number(maxTokens) } : undefined,
      });
      router.push(`/jobs/${job.short_id}`);
    } catch (err) {
      setError(String(err));
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <main>
      <div style={{ display: "grid", gridTemplateColumns: "1fr 340px", gap: 20 }}>
        <section className="panel">
          <h2 className="section">Recent jobs</h2>
          {error && <p style={{ color: "var(--red)" }}>{error}</p>}
          <table className="jobs">
            <thead>
              <tr>
                <th>ID</th>
                <th>Status</th>
                <th>Task</th>
                <th>Usage</th>
              </tr>
            </thead>
            <tbody>
              {jobs.map((j) => (
                <tr
                  key={j.id}
                  className="clickable"
                  onClick={() => router.push(`/jobs/${j.short_id}`)}
                >
                  <td className="mono">{j.short_id}</td>
                  <td>
                    <span className={`badge ${j.status}`}>{j.status}</span>
                    {j.attempt > 1 && (
                      <span style={{ color: "var(--muted)", fontSize: 12 }}>
                        {" "}
                        ×{j.attempt}
                      </span>
                    )}
                  </td>
                  <td>{j.title}</td>
                  <td className="mono" style={{ fontSize: 12 }}>
                    {(j.usage.tokens_in + j.usage.tokens_out).toLocaleString()} tok
                  </td>
                </tr>
              ))}
              {jobs.length === 0 && !error && (
                <tr>
                  <td colSpan={4} style={{ color: "var(--muted)" }}>
                    No jobs yet — submit one on the right.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </section>

        <form className="panel create" onSubmit={submit}>
          <h2 className="section">New job</h2>
          <label>Repository (URL or local path)</label>
          <input
            required
            value={repo}
            onChange={(e) => setRepo(e.target.value)}
            placeholder="https://github.com/you/repo"
          />
          <label>Prompt</label>
          <input
            required
            value={prompt}
            onChange={(e) => setPrompt(e.target.value)}
            placeholder="Fix issue BOS-123…"
          />
          <label>Token budget</label>
          <input
            value={maxTokens}
            onChange={(e) => setMaxTokens(e.target.value)}
            placeholder="120000"
          />
          <button className="primary" disabled={submitting}>
            {submitting ? "Enqueueing…" : "Run agent"}
          </button>
        </form>
      </div>
    </main>
  );
}

// Keep the event helper referenced for future live-list updates.
void subscribeEvents;
void TERMINAL;
