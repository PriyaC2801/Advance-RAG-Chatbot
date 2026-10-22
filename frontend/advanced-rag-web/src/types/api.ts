export type Dict = Record<string, string>;

export interface Technique { stage: string; name: string; description: string }
export interface ModeInfo { id: string; label: string; description: string }

export interface DocumentInfo {
  id: string;
  title: string;
  subtitle: string;
  description: string;
  fileName: string;
  techniques: Technique[];
  sampleQuestions: string[];
  modes: ModeInfo[];
  defaultMode: string | null;
  supportsEvaluation: boolean;
}

export type IndexState = 'NotIndexed' | 'Indexing' | 'Ready' | 'Failed';

export interface PipelineStatus {
  documentId: string;
  state: IndexState;
  pdfFound: boolean;
  pageCount: number;
  chunkCount: number;
  apiKeyConfigured: boolean;
  embeddingModel: string | null;
  indexedUtc: string | null;
  message: string | null;
  details: Dict;
}

export interface DocumentSummary { info: DocumentInfo; status: PipelineStatus }

export interface HealthResponse { status: string; geminiApiKeyConfigured: boolean; documents: string[] }

export type EvidenceStatus = 'Sufficient' | 'Limited' | 'Insufficient';

export interface Citation {
  label: string;
  chunkId: string;
  page: number | null;
  pageLabel: string | null;
  section: string | null;
  kind: string | null;
  excerpt: string;
  score: number | null;
  details: Dict | null;
}

export interface PipelineStep { name: string; description: string; durationMs: number | null; details: Dict | null }

export interface RankedPassage {
  label: string;
  chunkId: string;
  score: number;
  denseRank: number | null;
  keywordRank: number | null;
  notes: string[];
}

export interface AskDebug {
  pipeline: string;
  mode: string | null;
  steps: PipelineStep[];
  passages: RankedPassage[];
  model: string | null;
  systemPrompt: string | null;
  userMessage: string | null;
  totalMs: number;
  extra: Dict;
}

export interface AskResult {
  documentId: string;
  question: string;
  answer: string;
  evidenceStatus: EvidenceStatus | string;
  hasEvidence: boolean;
  citations: Citation[];
  debug: AskDebug;
}

export interface ChunkView {
  id: string;
  index: number;
  kind: string | null;
  page: number | null;
  pageEnd: number | null;
  section: string | null;
  text: string;
  metadata: Dict;
}
export interface ChunkPage { total: number; items: ChunkView[] }

export interface EvalCaseResult {
  id: string;
  question: string;
  passed: boolean;
  answer: string | null;
  failures: string[];
  metrics: Dict;
}
export interface EvalReport {
  documentId: string;
  mode: string | null;
  ranUtc: string;
  passed: number;
  total: number;
  cases: EvalCaseResult[];
  summary: Dict;
}

export interface ProblemDetails { title?: string; detail?: string; status?: number }
