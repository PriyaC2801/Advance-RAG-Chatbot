# advanced-rag-web

React 19 + Vite + TypeScript front end for the unified multi-document RAG chatbot (GEP, GPO, iMac pipelines behind one .NET API).

## Run

```
npm install
npm run dev      # http://localhost:5190 (strict port); /api is proxied to http://localhost:5090
npm run build    # strict tsc + production bundle
```

Start the API first (`dotnet run` in `backend/AdvancedRag.Api`, port 5090). If the Gemini key is missing:
`dotnet user-secrets set "Gemini:ApiKey" "YOUR_KEY"`.

## Layout

- `src/types/api.ts` mirrors the C# contracts (`Contracts.cs`)
- `src/services/apiClient.ts` typed fetch wrapper, `ApiError`, ProblemDetails mapping
- `src/hooks/useDocuments.ts` health + documents, polls every 2 s while indexing
- `src/pages/` Ask, How it works, Chunks, Evaluation
- `src/components/` answer with citation badges, debug panel, mode selector, shared UI bits

