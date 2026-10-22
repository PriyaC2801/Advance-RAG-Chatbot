import { useCallback, useEffect, useRef, useState } from 'react';
import { api, ApiError } from '../services/apiClient';
import type { DocumentSummary, HealthResponse } from '../types/api';

export interface DocumentsState {
  docs: DocumentSummary[];
  health: HealthResponse | null;
  unreachable: boolean;
  loading: boolean;
  error: string | null;
  refresh: () => Promise<void>;
  startIndex: (id: string, force: boolean, mode: string | null) => Promise<void>;
}

/** Loads health + documents, and polls every 2 s while any document is Indexing (5 s while the API is unreachable). */
export function useDocuments(): DocumentsState {
  const [docs, setDocs] = useState<DocumentSummary[]>([]);
  const [health, setHealth] = useState<HealthResponse | null>(null);
  const [unreachable, setUnreachable] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const alive = useRef(true);

  const refresh = useCallback(async () => {
    try {
      const [h, d] = await Promise.all([api.health(), api.documents()]);
      if (!alive.current) return;
      setHealth(h); setDocs(d); setUnreachable(false); setError(null);
    } catch (e) {
      if (!alive.current) return;
      if (e instanceof ApiError && e.unreachable) { setUnreachable(true); setHealth(null); }
      else setError(e instanceof Error ? e.message : String(e));
    } finally {
      if (alive.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    alive.current = true;
    void refresh();
    return () => { alive.current = false; };
  }, [refresh]);

  const anyIndexing = docs.some(x => x.status.state !== 'Ready');
  useEffect(() => {
    if (!anyIndexing && !unreachable) return;
    const t = setInterval(() => { void refresh(); }, unreachable ? 5000 : 2000);
    return () => clearInterval(t);
  }, [anyIndexing, unreachable, refresh]);

  const startIndex = useCallback(async (id: string, force: boolean, mode: string | null) => {
    await api.ingest(id, force, mode);
    setDocs(prev => prev.map(x => x.info.id === id
      ? { ...x, status: { ...x.status, state: 'Indexing', message: 'Starting...' } } : x));
    setTimeout(() => { void refresh(); }, 600);
  }, [refresh]);

  return { docs, health, unreachable, loading, error, refresh, startIndex };
}
