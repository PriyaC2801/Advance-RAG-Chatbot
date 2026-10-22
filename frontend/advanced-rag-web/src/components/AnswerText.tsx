import { Fragment, type ReactNode } from 'react';
import type { Citation } from '../types/api';

interface Props {
  text: string;
  citations: Citation[];
  onCitationClick: (label: string) => void;
}

const MARKER = /\[(S?\d+)\]/g;

/** Splits text on **bold** and renders as <strong>; remaining text gets citation badges. Never uses innerHTML. */
function renderInline(line: string, citations: Citation[], onClick: (l: string) => void, keyBase: string): ReactNode[] {
  const out: ReactNode[] = [];
  line.split(/(\*\*[^*]+\*\*)/g).forEach((part, i) => {
    if (!part) return;
    const bold = part.length > 4 && part.startsWith('**') && part.endsWith('**');
    const content = bold ? part.slice(2, -2) : part;
    const nodes = withBadges(content, citations, onClick, `${keyBase}-${i}`);
    out.push(bold ? <strong key={`${keyBase}-${i}`}>{nodes}</strong> : <Fragment key={`${keyBase}-${i}`}>{nodes}</Fragment>);
  });
  return out;
}

function withBadges(text: string, citations: Citation[], onClick: (l: string) => void, keyBase: string): ReactNode[] {
  const out: ReactNode[] = [];
  let last = 0;
  for (const m of text.matchAll(MARKER)) {
    const idx = m.index ?? 0;
    if (idx > last) out.push(text.slice(last, idx));
    const label = m[1];
    const cite = citations.find(c => c.label.replace(/[[\]]/g, '') === label);
    if (cite) {
      const tip = [cite.section, cite.page != null ? `p. ${cite.pageLabel ?? cite.page}` : null].filter(Boolean).join(' - ');
      out.push(
        <button key={`${keyBase}-${idx}`} type="button" className="cite-badge" title={tip || `Source ${label}`}
          aria-label={`Source ${label}${tip ? `: ${tip}` : ''}`} onClick={() => onClick(cite.label)}>
          {label}
        </button>,
      );
    } else {
      out.push(m[0]);
    }
    last = idx + m[0].length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

export function AnswerText({ text, citations, onCitationClick }: Props) {
  const paragraphs = text.replace(/\r\n/g, '\n').split(/\n{2,}/);
  return (
    <div className="answer-text">
      {paragraphs.map((p, pi) => (
        <p key={pi}>
          {p.split('\n').map((line, li, arr) => (
            <Fragment key={li}>
              {renderInline(line, citations, onCitationClick, `${pi}-${li}`)}
              {li < arr.length - 1 && <br />}
            </Fragment>
          ))}
        </p>
      ))}
    </div>
  );
}
