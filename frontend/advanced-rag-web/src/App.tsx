import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { AnswerText } from './components/AnswerText';
import { DebugPanel } from './components/DebugPanel';
import { ErrorBlock, Skeleton, Spinner } from './components/ui';
import { useDocuments } from './hooks/useDocuments';
import { api } from './services/apiClient';
import type { AskResult } from './types/api';

const ICONS: Record<string, string> = { gep: '🌍', gpo: '🏛️', imac: '🖥️' };

export default function App() {
  const { docs, unreachable, loading } = useDocuments();
  const [selected, setSelected] = useState<string | null>(null); // mandatory: nothing is preselected
  const [question, setQuestion] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [result, setResult] = useState<AskResult | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  useEffect(() => () => abortRef.current?.abort(), []);

  const doc = docs.find(d => d.info.id === selected) ?? null;
  const ready = doc?.status.state === 'Ready';

  const select = (id: string) => { setSelected(id); setResult(null); setError(null); setQuestion(''); };

  const submit = async () => {
    const text = question.trim();
    if (!doc || !ready || !text || busy) return;
    setBusy(true); setError(null); setResult(null);
    const ctl = new AbortController();
    abortRef.current = ctl;
    try {
      setResult(await api.ask(doc.info.id, text, null, ctl.signal));
    } catch (e) {
      if (!(e instanceof DOMException && e.name === 'AbortError')) setError(e);
    } finally {
      if (abortRef.current === ctl) abortRef.current = null;
      setBusy(false);
    }
  };

  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) { e.preventDefault(); void submit(); }
  };

  const hint = !doc ? 'Select a report above first'
    : !ready ? `${doc.info.title} is being prepared${doc.status.message ? `: ${doc.status.message}` : '...'}`
    : `Ask a question about ${doc.info.title}`;

  return (
    <div className="app">
      <header className="hero">
        <span className="hero-badge">RAG · 3 strategies</span>
        <h1>Advanced RAG Chatbot</h1>
        <p>Choose a report, ask a question, and see exactly how the answer was found.</p>
      </header>

      {unreachable && <div className="banner banner-bad" role="alert">Cannot reach the server. Retrying...</div>}

      <section className="step">
        <h2 className="step-title"><span className="step-no">1</span> Choose a report</h2>
        <nav className="doc-switcher" aria-label="Select a document">
          {loading && docs.length === 0 && <Skeleton lines={2} />}
          {docs.map(d => {
            const on = d.info.id === selected;
            const isReady = d.status.state === 'Ready';
            return (
              <button key={d.info.id} type="button" className={`doc-card${on ? ' doc-on' : ''}`}
                aria-pressed={on} onClick={() => select(d.info.id)}>
                <span className="doc-icon" aria-hidden="true">{ICONS[d.info.id] ?? '📄'}</span>
                <strong>{d.info.title}</strong>
                <span className="muted small">{d.info.subtitle}</span>
                <span className={`doc-state ${isReady ? 'state-ok' : 'state-wait'}`}>{isReady ? 'Ready' : 'Preparing…'}</span>
                {on && <span className="doc-check" aria-hidden="true">✓</span>}
              </button>
            );
          })}
        </nav>
      </section>

      <section className="step">
        <h2 className="step-title"><span className="step-no">2</span> Ask a question</h2>
        <form className="card ask-form" onSubmit={e => { e.preventDefault(); void submit(); }}>
          <label htmlFor="question" className="label">{hint}</label>
          <textarea
            id="question" rows={3} value={question} maxLength={1000} disabled={!ready}
            placeholder="Type a question... (Enter to send, Shift+Enter for a new line)"
            onChange={e => setQuestion(e.target.value)} onKeyDown={onKey}
          />
          {doc && ready && doc.info.sampleQuestions.length > 0 && (
            <div className="samples">
              <span className="muted small">Try a sample question:</span>
              <div className="chips">
                {doc.info.sampleQuestions.map(q => (
                  <button key={q} type="button" className="chip chip-button" disabled={busy}
                    onClick={() => setQuestion(q)}>{q}</button>
                ))}
              </div>
            </div>
          )}
          <div className="row">
            <button type="submit" className="btn btn-primary btn-ask" disabled={!ready || busy || !question.trim()}>
              {busy ? <Spinner label="Thinking..." /> : 'Ask ➤'}
            </button>
            {busy && <button type="button" className="btn" onClick={() => abortRef.current?.abort()}>Cancel</button>}
          </div>
        </form>
      </section>

      {error != null && <ErrorBlock error={error} />}
      {busy && <div className="card" aria-live="polite"><Skeleton lines={4} /></div>}

      {result && !busy && (
        <section className="step">
          <h2 className="step-title"><span className="step-no">3</span> Answer</h2>
          <div className="card answer-card" aria-live="polite" aria-label="Answer">
            <p className="asked">“{result.question}”</p>
            <AnswerText text={result.answer} citations={result.citations} onCitationClick={() => undefined} />
          </div>
          <DebugPanel result={result} />
        </section>
      )}

      <footer className="foot muted small">Answers are generated only from the selected report.</footer>
    </div>
  );
}
