export const RELAY_URL =
  process.env.NEXT_PUBLIC_RELAY_URL ?? "http://localhost:8080";

// ---- domain types mirroring Relay.Core wire format (snake_case) ----

export type Budget = {
  max_tokens?: number;
  max_cost_usd?: number;
  max_runtime_seconds?: number;
};

export type Usage = {
  tokens_in: number;
  tokens_out: number;
  cost_usd: number;
  tool_calls: number;
  retries: number;
};

export type JobResult = {
  branch?: string;
  diff_stat?: string;
  changed_files?: string[];
  commit_sha?: string;
  pr_url?: string;
  tests_passed: boolean;
};

export type Job = {
  id: string;
  short_id: string;
  title: string;
  repo_url: string;
  prompt: string;
  agent: string;
  status: string;
  budget?: Budget | null;
  usage: Usage;
  attempt: number;
  failure_reason?: string | null;
  result?: JobResult | null;
  created_at: string;
};

export type JobEventDto = {
  seq: number;
  kind: string;
  message: string;
  data?: Record<string, string>;
  created_at: string;
};

export const TERMINAL = new Set(["completed", "failed", "cancelled"]);

export async function listJobs(limit = 25): Promise<Job[]> {
  const res = await fetch(`${RELAY_URL}/api/jobs?limit=${limit}`, {
    cache: "no-store",
  });
  if (!res.ok) throw new Error(`list failed: ${res.status}`);
  return res.json();
}

export async function getJob(idOrShort: string): Promise<Job> {
  const res = await fetch(`${RELAY_URL}/api/jobs/${idOrShort}`, {
    cache: "no-store",
  });
  if (!res.ok) throw new Error(`job not found`);
  return res.json();
}

export async function createJob(req: {
  repo_url: string;
  prompt: string;
  budget?: Budget;
}): Promise<Job> {
  const res = await fetch(`${RELAY_URL}/api/jobs`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(req),
  });
  if (!res.ok) throw new Error(`create failed: ${await res.text()}`);
  return res.json();
}

export async function cancelJob(idOrShort: string): Promise<boolean> {
  const res = await fetch(`${RELAY_URL}/api/jobs/${idOrShort}/cancel`, {
    method: "POST",
  });
  return res.ok;
}

/** Subscribe to the SSE event stream; returns a cleanup function. */
export function subscribeEvents(
  idOrShort: string,
  onEvent: (e: JobEventDto) => void,
): () => void {
  const es = new EventSource(`${RELAY_URL}/api/jobs/${idOrShort}/events`);
  es.onmessage = (msg) => {
    try {
      onEvent(JSON.parse(msg.data));
    } catch {
      /* ignore malformed frames */
    }
  };
  return () => es.close();
}
