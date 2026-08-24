"use client";

import { useEffect, useRef, useState } from "react";
import { useParams } from "next/navigation";
import {
  cancelJob,
  getJob,
  subscribeEvents,
  TERMINAL,
  type Job,
  type JobEventDto,
} from "@/lib/relay";

const STEPS = ["queued", "preparing", "running", "validating", "completed"];

export default function JobDetail() {
  const params = useParams<{ id: string }>();
  const shortId = params.id;

  const [job, setJob] = useState<Job | null>(null);
  const [events, setEvents] = useState<JobEventDto[]>([]);
  const [error, setError] = useState<string | null>(null);
  const logRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    getJob(shortId).then(setJob).catch((e) => setError(String(e)));
    const unsubscribe = subscribeEvents(shortId, (e) => {
      if (e.kind === "state") {
        // refresh the job snapshot on every transition
        getJob(shortId).then(setJob).catch(() => {});
      }
      setEvents((prev) =>
        prev.length > 0 && prev[prev.length - 1].seq >= e.seq
          ? prev
          : [...prev, e],
      );
    });
    return () => unsubscribe();
  }, [shortId]);

  useEffect(() => {
    logRef.current?.scrollTo({ top: logRef.current.scrollHeight });
  }, [events]);

  async function doCancel() {
    await cancelJob(shortId);
  }

  if (error)
    return (
      <main>
        <p style={{ color: "var(--red)" }}>{error}</p>
      </main>
    );

  const status = job?.status ?? "…";
  const stepIndex = STEPS.indexOf(status);
  const usage = job?.usage;
  const budget = job?.budget;
  const tokenPct =
    budget?.max_tokens && usage
      ? Math.min(
          100,
          ((usage.tokens_in + usage.tokens_out) / budget.max_tokens) * 100,
        )
      : 0;
  const isTerminal = TERMINAL.has(status);

  return (
    <main>
      <div className="panel">
        <div style={{ display: "flex", justifyContent: "space-between" }}>
          <div>
            <h2 className="section">Job {shortId}</h2>
            <div style={{ fontSize: 18, fontWeight: 600 }}>
              {job?.title ?? "loading…"}
            </div>
            <div className="mono" style={{ color: "var(--muted)", fontSize: 12, marginTop: 4 }}>
              {job?.repo_url} · agent: {job?.agent ?? "?"}
            </div>
          </div>
          <div>
            <span className={`badge ${status}`}>{status}</span>
            {!isTerminal && (
              <button
                onClick={doCancel}
                style={{
                  marginLeft: 10, background: "none", color: "var(--red)",
                  border: "1px solid var(--red)", borderRadius: 6,
                  padding: "3px 10px", cursor: "pointer",
                }}
              >
                cancel
              </button>
            )}
          </div>
        </div>

        <div className="stepper" style={{ marginTop: 16 }}>
          {STEPS.map((s, i) => (
            <span key={s} style={{ display: "contents" }}>
              {i > 0 && <span className="arrow">→</span>}
              <span
                className={`step ${
                  status === s || (isTerminal && status === "completed")
                    ? "current"
                    : i < stepIndex || (isTerminal && status === "completed")
                      ? "done"
                      : ""
                }`}
              >
                {s}
              </span>
            </span>
          ))}
          {(status === "interrupted" || status === "recovering") && (
            <>
              <span className="arrow">⟳</span>
              <span className="step current">{status}</span>
            </>
          )}
        </div>
        {job && !isTerminal && (
          <p style={{ color: "var(--muted)", fontSize: 13, marginTop: 12 }}>
            If the worker dies now, Relay recovers this job from its last checkpoint.
          </p>
        )}
      </div>

      <div style={{ display: "grid", gridTemplateColumns: "1fr 320px", gap: 20 }}>
        <section className="panel">
          <h2 className="section">Event stream</h2>
          <div className="log" ref={logRef}>
            {events.map((e) => (
              <div key={e.seq} className={e.kind}>
                {e.kind === "state"
                  ? `[${e.data?.from}] → [${e.data?.to}]${e.data?.reason ? ` — ${e.data.reason}` : ""}`
                  : `${e.kind === "log" ? "" : e.kind + ": "}${e.message}`}
              </div>
            ))}
            {events.length === 0 && (
              <div className="plain">waiting for events…</div>
            )}
          </div>
        </section>

        <aside>
          {usage && (
            <section className="panel">
              <h2 className="section">Budget</h2>
              <div style={{ fontSize: 14 }}>
                Tokens {(usage.tokens_in + usage.tokens_out).toLocaleString()}
                {budget?.max_tokens
                  ? ` / ${budget.max_tokens.toLocaleString()}`
                  : ""}
              </div>
              <div className={`meter ${tokenPct > 90 ? "danger" : ""}`}>
                <div style={{ width: `${tokenPct}%` }} />
              </div>
              <dl className="kv" style={{ marginTop: 12 }}>
                <dt>Cost est.</dt>
                <dd>${Number(usage.cost_usd).toFixed(2)}{budget?.max_cost_usd ? ` / $${budget.max_cost_usd}` : ""}</dd>
                <dt>Tool calls</dt>
                <dd>{usage.tool_calls}</dd>
                <dt>Retries</dt>
                <dd>{usage.retries}</dd>
              </dl>
            </section>
          )}

          {job?.result && (
            <section className="panel">
              <h2 className="section">Result</h2>
              <dl className="kv">
                <dt>Branch</dt>
                <dd>{job.result.branch}</dd>
                <dt>Changed</dt>
                <dd>{job.result.changed_files?.join(", ") || "—"}</dd>
                <dt>Tests</dt>
                <dd style={{ color: job.result.tests_passed ? "var(--green)" : "var(--red)" }}>
                  {job.result.tests_passed ? "passed" : "failed"}
                </dd>
                {job.result.pr_url && (
                  <>
                    <dt>Pull req</dt>
                    <dd>
                      <a href={job.result.pr_url}>{job.result.pr_url}</a>
                    </dd>
                  </>
                )}
              </dl>
            </section>
          )}

          {job?.failure_reason && (
            <section className="panel">
              <h2 className="section" style={{ color: "var(--red)" }}>
                Failure
              </h2>
              <p style={{ margin: 0, fontSize: 14 }}>{job.failure_reason}</p>
            </section>
          )}

          {status === "interrupted" && (
            <section className="panel">
              <h2 className="section" style={{ color: "var(--yellow)" }}>
                Recovering
              </h2>
              <p style={{ margin: 0, fontSize: 14 }}>
                Worker lost mid-flight. The lease expired; Relay will requeue
                this job and a healthy worker resumes from the last checkpoint.
              </p>
            </section>
          )}
        </aside>
      </div>
    </main>
  );
}
