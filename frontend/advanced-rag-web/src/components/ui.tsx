import { useState, type ReactNode } from 'react';
import { describeError } from '../services/apiClient';
import type { Dict } from '../types/api';

export function Spinner({ label }: { label?: string }) {
  return (
    <span className="spinner-wrap" role="status">
      <span className="spinner" aria-hidden="true" />
      {label && <span>{label}</span>}
    </span>
  );
}

export function Chip({ children, tone = 'neutral', title }: { children: ReactNode; tone?: 'neutral' | 'ok' | 'warn' | 'bad' | 'info'; title?: string }) {
  return <span className={`chip chip-${tone}`} title={title}>{children}</span>;
}

export function KeyValues({ data }: { data?: Dict | null }) {
  const entries = Object.entries(data ?? {});
  if (entries.length === 0) return null;
  return (
    <dl className="kv">
      {entries.map(([k, v]) => (
        <div key={k} className="kv-row">
          <dt>{k}</dt>
          <dd>{v}</dd>
        </div>
      ))}
    </dl>
  );
}

export function CopyButton({ text }: { text: string }) {
  const [done, setDone] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text);
      setDone(true);
      setTimeout(() => setDone(false), 1500);
    } catch { /* clipboard unavailable */ }
  };
  return <button type="button" className="btn btn-small" onClick={copy}>{done ? 'Copied' : 'Copy'}</button>;
}

export function Collapsible({ title, children, defaultOpen = false, actions }: {
  title: ReactNode; children: ReactNode; defaultOpen?: boolean; actions?: ReactNode;
}) {
  return (
    <details className="collapsible" open={defaultOpen}>
      <summary>
        <span className="summary-title">{title}</span>
        {actions && <span className="summary-actions" onClick={e => e.preventDefault()}>{actions}</span>}
      </summary>
      <div className="collapsible-body">{children}</div>
    </details>
  );
}

export function CodeBlock({ title, text }: { title: string; text: string }) {
  return (
    <Collapsible title={title} actions={<CopyButton text={text} />}>
      <pre className="code">{text}</pre>
    </Collapsible>
  );
}

export function ErrorBlock({ error, onIndex }: { error: unknown; onIndex?: () => void }) {
  const { title, detail, status } = describeError(error);
  let friendly = '';
  if (status === 409) friendly = 'The index is not ready for this document (or mode). Index it first, then try again.';
  else if (status === 429) friendly = 'The Gemini quota was exceeded. Wait a minute and retry, or use a key with a higher quota.';
  else if (status === 503) friendly = 'The service is unavailable: the Gemini key or the database may be missing or unreachable. Check the API console.';
  else if (status === 502 || status === 504) friendly = 'The upstream model service failed or timed out. Retrying usually helps.';
  return (
    <div className="error-block" role="alert">
      <strong>{title}</strong>
      {friendly && <p>{friendly}</p>}
      {detail && <p className="muted">{detail}</p>}
      {status === 409 && onIndex && <button type="button" className="btn btn-primary btn-small" onClick={onIndex}>Index now</button>}
    </div>
  );
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <h3>{title}</h3>
      {children}
    </div>
  );
}

export function Skeleton({ lines = 3 }: { lines?: number }) {
  return (
    <div className="skeleton" aria-hidden="true">
      {Array.from({ length: lines }, (_, i) => <div key={i} className="skeleton-line" style={{ width: `${95 - i * 12}%` }} />)}
    </div>
  );
}

export const formatMs = (ms: number | null | undefined) =>
  ms == null ? '' : ms >= 1000 ? `${(ms / 1000).toFixed(1)} s` : `${ms} ms`;

export const formatScore = (n: number | null | undefined) => (n == null ? '' : n.toFixed(3));
