import type {
  AskResult, ChunkPage, DocumentSummary, EvalReport, HealthResponse, PipelineStatus, ProblemDetails,
} from '../types/api';

export class ApiError extends Error {
  readonly status: number;
  readonly detail: string;
  /** true when the request never reached the API (network error / proxy error with empty body) */
  readonly unreachable: boolean;
  constructor(status: number, title: string, detail = '', unreachable = false) {
    super(title);
    this.name = 'ApiError';
    this.status = status;
    this.detail = detail;
    this.unreachable = unreachable;
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(path, init);
  } catch (e) {
    if (e instanceof DOMException && e.name === 'AbortError') throw e;
    throw new ApiError(0, 'Cannot reach the API', 'The request failed before getting a response.', true);
  }
  if (!res.ok) {
    let p: ProblemDetails = {};
    const text = await res.text().catch(() => '');
    try { p = text ? (JSON.parse(text) as ProblemDetails) : {}; } catch { /* not json */ }
    // The Vite proxy answers with an empty 5xx body when the target is down.
    const unreachable = !text && (res.status === 500 || res.status === 502 || res.status === 504);
    throw new ApiError(
      res.status,
      p.title ?? (unreachable ? 'Cannot reach the API' : `Request failed (${res.status})`),
      p.detail ?? (unreachable ? '' : text.slice(0, 300)),
      unreachable,
    );
  }
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

const json = (body: unknown, signal?: AbortSignal): RequestInit => ({
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
  signal,
});

const qs = (params: Record<string, string | number | undefined | null>) => {
  const s = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== '') s.set(k, String(v));
  const out = s.toString();
  return out ? `?${out}` : '';
};

const d = (id: string) => `/api/documents/${encodeURIComponent(id)}`;

export const api = {
  health: (signal?: AbortSignal) => request<HealthResponse>('/api/health', { signal }),
  documents: (signal?: AbortSignal) => request<DocumentSummary[]>('/api/documents', { signal }),
  status: (id: string, signal?: AbortSignal) => request<PipelineStatus>(`${d(id)}/status`, { signal }),
  ingest: (id: string, force: boolean, mode?: string | null) =>
    request<unknown>(`${d(id)}/ingest${qs({ force: force ? 'true' : 'false', mode })}`, { method: 'POST' }),
  chunks: (
    id: string,
    q: { kind?: string; page?: number | null; contains?: string; skip?: number; take?: number },
    signal?: AbortSignal,
  ) => request<ChunkPage>(`${d(id)}/chunks${qs(q)}`, { signal }),
  ask: (documentId: string, question: string, mode: string | null, signal?: AbortSignal) =>
    request<AskResult>('/api/chat/ask', json({ documentId, question, mode }, signal)),
  runEvaluation: (id: string, mode: string | null, signal?: AbortSignal) =>
    request<EvalReport>(`${d(id)}/evaluation/run${qs({ mode, repeat: 1 })}`, { method: 'POST', signal }),
  latestEvaluations: (id: string, signal?: AbortSignal) =>
    request<EvalReport[]>(`${d(id)}/evaluation/latest`, { signal }),
  pdfUrl: (id: string, page?: number | null) => `${d(id)}/pdf${page ? `#page=${page}` : ''}`,
};

export function describeError(e: unknown): { title: string; detail: string; status: number } {
  if (e instanceof ApiError) return { title: e.message, detail: e.detail, status: e.status };
  if (e instanceof Error) return { title: e.message, detail: '', status: 0 };
  return { title: 'Unexpected error', detail: String(e), status: 0 };
}
