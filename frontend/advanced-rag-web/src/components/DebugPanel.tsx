import type { AskResult } from '../types/api';
import { Chip, CodeBlock, Collapsible, formatMs, formatScore, KeyValues } from './ui';

export function DebugPanel({ result }: { result: AskResult }) {
  const debug = result.debug;
  const maxMs = Math.max(1, ...debug.steps.map(s => s.durationMs ?? 0));
  return (
    <Collapsible
      title={<strong>How this answer was generated</strong>}
      defaultOpen
    >
      <p className="muted">
        Pipeline: <strong>{debug.pipeline}</strong>
        {debug.mode && <> - mode <strong>{debug.mode}</strong></>}
        {debug.model && <> - model <strong>{debug.model}</strong></>} - total {formatMs(debug.totalMs)}
      </p>

      <div className="answer-meta">
        <Chip tone={result.evidenceStatus === 'Sufficient' ? 'ok' : result.evidenceStatus === 'Limited' ? 'warn' : 'bad'}>
          Evidence: {result.evidenceStatus}
        </Chip>
      </div>

      <h4>Sources ({result.citations.length})</h4>
      {result.citations.length === 0 && <p className="muted">No sources were cited.</p>}
      <div className="sources">
        {result.citations.map(c => (
          <article key={c.label + c.chunkId} className="source">
            <header className="source-head">
              <span className="cite-badge static">{c.label}</span>
              {c.page != null && <strong>p. {c.pageLabel && c.pageLabel !== String(c.page) ? `${c.pageLabel} (PDF ${c.page})` : c.page}</strong>}
              {c.kind && <Chip>{c.kind}</Chip>}
              {c.score != null && <span className="muted small">score {formatScore(c.score)}</span>}
            </header>
            {c.section && <div className="source-section">{c.section}</div>}
            <p className="excerpt">{c.excerpt}</p>
            <KeyValues data={c.details} />
          </article>
        ))}
      </div>

      <h4>Steps</h4>
      <ol className="timeline">
        {debug.steps.map((s, i) => (
          <li key={i}>
            <div className="timeline-head">
              <strong>{s.name}</strong>
              {s.durationMs != null && <span className="muted small">{formatMs(s.durationMs)}</span>}
            </div>
            {s.durationMs != null && (
              <div className="bar" aria-hidden="true"><span style={{ width: `${Math.max(2, (s.durationMs / maxMs) * 100)}%` }} /></div>
            )}
            <p>{s.description}</p>
            <KeyValues data={s.details} />
          </li>
        ))}
      </ol>

      {debug.passages.length > 0 && (
        <>
          <h4>Ranked passages</h4>
          <div className="table-wrap">
            <table>
              <thead>
                <tr><th>Label</th><th>Chunk</th><th>Score</th><th>Dense rank</th><th>Keyword rank</th><th>Notes</th></tr>
              </thead>
              <tbody>
                {debug.passages.map(p => (
                  <tr key={p.label + p.chunkId}>
                    <td>{p.label}</td>
                    <td><code>{p.chunkId}</code></td>
                    <td>{formatScore(p.score)}</td>
                    <td>{p.denseRank ?? '-'}</td>
                    <td>{p.keywordRank ?? '-'}</td>
                    <td>{p.notes.length > 0 ? <ul className="notes">{p.notes.map((n, i) => <li key={i}>{n}</li>)}</ul> : ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      {debug.systemPrompt && <CodeBlock title="System prompt" text={debug.systemPrompt} />}
      {debug.userMessage && <CodeBlock title="User message (question + retrieved context)" text={debug.userMessage} />}

      {Object.keys(debug.extra).length > 0 && (
        <>
          <h4>Extra</h4>
          <KeyValues data={debug.extra} />
        </>
      )}
    </Collapsible>
  );
}
