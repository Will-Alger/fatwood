// What the "How it works" tab draws. Every claim here comes from README.md and
// docs/ (operations, accounts, search-quality); keep them in step. Code paths
// drop the `src/ResearchDiscovery.` prefix so they fit on a chip.

export type StepKind = 'you' | 'llm' | 'local' | 'store' | 'nightly' | 'platform' | 'source' | 'ci'

export const KIND_LABEL: Record<StepKind, string> = {
  you: 'You',
  llm: 'LLM call',
  local: 'Local',
  store: 'Stored',
  nightly: 'Nightly',
  platform: 'Platform',
  source: 'External',
  ci: 'CI',
}

export interface Step {
  id: string
  kind: StepKind
  title: string
  /** One line on the card. */
  blurb: string
  /** The open panel: a paragraph or two in plain English. */
  body: string[]
  facts?: string[]
  code?: string[]
  /** A hop into another lane, shown as a button on the panel. */
  link?: { label: string; to: string }
}

export interface Edge {
  from: string
  to: string
}

export interface Lane {
  id: string
  title: string
  subtitle: string
  /** Columns left to right; a column with two steps stacks them. */
  columns: string[][]
  edges: Edge[]
  /** The trace animation: each beat lights these edges together. */
  trace: number[][]
  traceLabel: string
}

export const STEPS: Record<string, Step> = {
  prompt: {
    id: 'prompt',
    kind: 'you',
    title: 'Your prompt',
    blurb: 'A goal in plain English, plus your profile.',
    body: [
      'You say what you want to build and where you are starting from — “a weekend anomaly-detection project, 4 years of Java”. Your saved profile (experience, goals, hours a week) rides along, so results and analyses fit you rather than an average reader.',
    ],
    facts: [
      'Anyone can browse; searching needs an account with budget',
      'Profiles are versioned — editing yours marks old analyses stale',
    ],
    code: ['web/src/components/Discover.tsx', 'Api/Controllers/SearchController.cs'],
  },
  plan: {
    id: 'plan',
    kind: 'llm',
    title: 'Search plan',
    blurb: 'One call turns words into topics, fields and dates.',
    body: [
      'Claude reads your prompt once and returns a structured plan: 8–15 anchor topics, a short made-up abstract of the paper you would want (HyDE), the arXiv categories to search and a date window.',
      'The plan comes back as editable chips. Change one and the search re-runs without another LLM call.',
    ],
    facts: [
      'The only LLM call in a search — priced and logged to your ledger',
      'Structured output, so the plan always parses',
      'The model is chosen per step in Settings',
    ],
    code: ['Infrastructure/Search/AnthropicSearchPlanCompiler.cs', 'Infrastructure/Llm/AnthropicCallFactory.cs'],
  },
  dense: {
    id: 'dense',
    kind: 'local',
    title: 'Meaning search',
    blurb: 'Embeddings find papers that mean the same thing.',
    body: [
      'The plan’s topics are embedded on the server with the same small model used for every abstract. Each topic scores the candidate papers by similarity, and a paper keeps its best topic match — so a goal with several parts is not averaged into mush.',
      'Category and date filters narrow the candidates first, from a set cached in memory.',
    ],
    facts: [
      'bge-small via ONNX: 384 numbers per text',
      'Vectors held as int8, so ~925k papers fit in a 4 GiB replica',
      'The scan runs across every core — no LLM, no network',
    ],
    code: [
      'Infrastructure/Embeddings/InMemoryEmbeddingIndex.cs',
      'Infrastructure/Embeddings/OnnxTextEmbedder.cs',
      'Infrastructure/Search/CandidateSetCache.cs',
    ],
    link: { label: 'Loaded from the index snapshot', to: 'snapshot' },
  },
  lexical: {
    id: 'lexical',
    kind: 'local',
    title: 'Keyword search',
    blurb: 'BM25 catches the exact terms embeddings blur.',
    body: [
      'Alongside, a classic BM25 keyword index scores the same candidates. It rewards rare, exact words — acronyms, model names, jargon like “LoRA” or “SLAM” — that embeddings tend to smear together.',
    ],
    facts: ['An in-memory inverted index, restored from a snapshot at startup'],
    code: ['Infrastructure/Search/InMemoryLexicalIndex.cs'],
    link: { label: 'Loaded from the index snapshot', to: 'snapshot' },
  },
  fuse: {
    id: 'fuse',
    kind: 'local',
    title: 'Rank fusion',
    blurb: 'Two rankings become one with RRF.',
    body: [
      'Reciprocal Rank Fusion merges the two lists by position alone: a paper scores 1 / (60 + rank) in each list and the scores add. A paper near the top of both wins, and the two systems’ very different raw scores never have to be compared.',
    ],
    facts: [
      'Hybrid search: +17% nDCG@10 over embeddings alone',
      'A cross-encoder reranker was tried here and dropped — no measurable gain',
    ],
    code: ['Infrastructure/Search/SearchService.cs'],
  },
  shortlist: {
    id: 'shortlist',
    kind: 'you',
    title: 'Ranked shortlist',
    blurb: 'Your top results, two of them wildcards.',
    body: [
      'Relevance is the only thing that orders results. Your experience never filters them; it only labels a hit “close to home” or “stretch”.',
      'Two slots are held for wildcards: highly relevant papers furthest from what you already know, so your comfort zone cannot quietly narrow what you see.',
    ],
    facts: [
      'Picked from a pool five times the page size',
      'Searches are logged for telemetry and A/B interleaving',
    ],
    code: ['Infrastructure/Search/SearchService.cs', 'web/src/components/PaperList.tsx'],
    link: { label: 'Pick a paper to analyze', to: 'estimate' },
  },

  estimate: {
    id: 'estimate',
    kind: 'local',
    title: 'Price check',
    blurb: 'You see the dollar cost before you spend it.',
    body: [
      'Choose papers to analyze and the button shows a live estimate — “Analyze top 25 — est. $0.05” — priced from the model registry.',
      'Before any call is made, the server checks you are signed in, past the invite gate and have budget left.',
    ],
    facts: [
      'Every account starts with a $1 grant — roughly a thousand analyses',
      'A daily call cap and rate limits sit on every spending route',
      'Bring your own Anthropic key and the spend bills to you instead',
    ],
    code: [
      'Infrastructure/Llm/LlmSettingsService.cs',
      'Infrastructure/Accounts/BudgetService.cs',
      'Api/Auth/AuthPolicies.cs',
    ],
  },
  queue: {
    id: 'queue',
    kind: 'platform',
    title: 'Analysis queue',
    blurb: 'The first papers run now; the rest scale out.',
    body: [
      'The first few papers of a request run in-process — the hot lane — so results start appearing within seconds. The rest go onto an Azure Storage queue, drained by a worker job that KEDA scales up from zero when there is work.',
    ],
    facts: [
      'Each paper is saved as it finishes: a cancelled run keeps what it paid for',
      'Re-runs skip papers already current for your profile',
    ],
    code: [
      'Infrastructure/Analysis/HybridAnalysisQueue.cs',
      'Infrastructure/Analysis/StorageAnalysisQueue.cs',
      'Api/Cli/AnalysisWorkerRunner.cs',
    ],
  },
  analyze: {
    id: 'analyze',
    kind: 'llm',
    title: 'Feasibility read',
    blurb: 'One call per paper, judged against you.',
    body: [
      'Claude reads the paper against your profile and returns a structured verdict: hard blockers, what you would learn, effort, reproduce or extend, whether reference code likely exists, goal fit, resume signal, an extension idea and the skills it needs.',
      'A 0–100 composite score makes analyzed papers sortable in Browse.',
    ],
    facts: [
      'Cached per paper × profile version',
      'A paper the model declines is recorded and skipped at no cost',
    ],
    code: ['Infrastructure/Analysis/AnthropicPaperAnalyzer.cs', 'Infrastructure/Analysis/AnalysisContract.cs'],
  },
  ledger: {
    id: 'ledger',
    kind: 'store',
    title: 'Budget ledger',
    blurb: 'Real token counts, in micro-dollars.',
    body: [
      'Every Anthropic call records the token counts the response reports — never estimates — priced from the model registry as an append-only debit. Credits are grant rows.',
      'Your remaining budget is always worked out from the two and never stored, so it cannot drift.',
    ],
    facts: ['Integer micro-dollars, never floats', 'A future purchase is just another grant row'],
    code: ['Infrastructure/Accounts/LlmUsageRecorder.cs', 'Infrastructure/Accounts/BudgetService.cs'],
  },

  arxiv: {
    id: 'arxiv',
    kind: 'source',
    title: 'arXiv',
    blurb: 'The open archive every paper comes from.',
    body: [
      'Papers come from arXiv’s public APIs. The daily delta uses the query API; the decade of history was bulk-harvested over OAI-PMH, resuming from a token whenever it stalled.',
      'Requests are throttled to arXiv’s one-every-three-seconds etiquette.',
    ],
    facts: ['~925k papers, growing every night', 'Thank you to arXiv for use of its open access interoperability'],
    code: [
      'Infrastructure/Arxiv/ArxivClient.cs',
      'Infrastructure/Arxiv/ArxivOaiClient.cs',
      'Infrastructure/Arxiv/ArxivThrottlingHandler.cs',
    ],
  },
  ingest: {
    id: 'ingest',
    kind: 'nightly',
    title: 'Ingestion',
    blurb: 'A nightly job fetches only what is new.',
    body: [
      'A cron job on the same Docker image runs at 06:30 UTC. For each category it fetches only papers newer than that category’s high-water mark, so a missed night heals itself.',
      'Writes are keyed on the arXiv ID: run it twice and nothing duplicates.',
    ],
    facts: [
      'A lease row in the database stops two runs from overlapping',
      'Also runnable by hand, from the CLI or the admin page',
    ],
    code: [
      'Infrastructure/Ingestion/IngestionService.cs',
      'Infrastructure/Ingestion/DbIngestionLockManager.cs',
      'Api/Cli/IngestCommandRunner.cs',
    ],
  },
  postgres: {
    id: 'postgres',
    kind: 'store',
    title: 'PostgreSQL',
    blurb: 'Papers, people, analyses and the ledger.',
    body: [
      'One database holds the corpus, accounts, profiles, bookmarks, analyses, search telemetry and the budget ledger, through EF Core.',
      'It is kept portable to SQL Server: no raw SQL, no Postgres-only types.',
    ],
    facts: ['Citation counts and GitHub stars are enriched in, refreshed every 14 days'],
    code: ['Infrastructure/Persistence', 'Infrastructure/Enrichment/PaperSignalEnricher.cs'],
  },
  embed: {
    id: 'embed',
    kind: 'local',
    title: 'Embeddings',
    blurb: 'Every new abstract becomes 384 numbers.',
    body: [
      'At the end of each ingest, new papers are embedded inside the .NET process with bge-small via ONNX — no external API, no cost per paper — and quantized to int8.',
      'Which papers still need a vector is worked out from state, so anything missed is picked up on the next run.',
    ],
    facts: ['A ~130 MB model, downloaded on first use', 'Progress saves every 512 papers, so an interrupted run resumes'],
    code: ['Infrastructure/Embeddings/PaperEmbeddingService.cs', 'Infrastructure/Embeddings/Int8Quantization.cs'],
  },
  snapshot: {
    id: 'snapshot',
    kind: 'store',
    title: 'Index snapshot',
    blurb: 'Both indexes, ready for a cold start.',
    body: [
      'After embedding, the int8 vectors and the BM25 postings are written to blob storage. A fresh API replica downloads them in seconds instead of rebuilding from Postgres, which stays the fallback.',
      'Snapshots stream through a temp file both ways, so a replica never holds two full copies in memory.',
    ],
    code: ['Infrastructure/Search/SearchIndexSnapshotWriter.cs', 'Infrastructure/Search/SearchIndexSnapshotStore.cs'],
    link: { label: 'Serves meaning search', to: 'dense' },
  },

  edge: {
    id: 'edge',
    kind: 'platform',
    title: 'Cloudflare',
    blurb: 'TLS, bot checks and the front door.',
    body: [
      'Cloudflare fronts fatwood.io with strict TLS, Bot Fight Mode and an edge redirect to www.',
      'Behind it, the API adds per-caller rate limits (tighter on spending and sign-in), a strict content security policy and HSTS.',
    ],
    code: ['Api/Program.cs'],
  },
  auth: {
    id: 'auth',
    kind: 'platform',
    title: 'Sign-in',
    blurb: 'Entra External ID, drawn inside the app.',
    body: [
      'Passwords never touch this codebase: identity lives in Microsoft Entra External ID, but the sign-in screens are rendered in the app through a same-origin, allowlisted proxy.',
      'Tokens carry identity only. Roles and access are looked up in the database on every request, so being an admin is a database fact, not a token claim.',
    ],
    facts: [
      'Verification email comes from noreply@fatwood.io, not a stock template',
      'Production refuses to start if auth is not configured',
    ],
    code: ['Api/Auth/NativeAuthProxy.cs', 'Api/Auth/UserContextMiddleware.cs', 'web/src/components/AuthPanel.tsx'],
  },
  eval: {
    id: 'eval',
    kind: 'ci',
    title: 'Eval gate',
    blurb: 'Ranking changes have to beat the numbers.',
    body: [
      'An offline harness scores the ranker against 54 frozen queries and ~8,200 graded relevance judgments. CI fails any change that drops nDCG@10 below 0.590; today’s ranker scores 0.628.',
      'Several ideas that seemed obviously good were dropped because the numbers said so.',
    ],
    facts: ['Also tracks recall and MRR', 'Live searches can A/B two rankers by interleaving'],
    code: ['Infrastructure/Eval/EvalRunner.cs', 'eval/queries.json', '.github/workflows/ci.yml'],
  },
  cicd: {
    id: 'cicd',
    kind: 'ci',
    title: 'Deploy',
    blurb: 'Every push to main ships itself.',
    body: [
      'GitHub Actions builds one Docker image and deploys it to Azure. It signs in to Azure with OIDC, so no long-lived cloud secrets live in the repo, and every resource is declared in Bicep.',
    ],
    code: ['.github/workflows/cd.yml', 'infra/main.bicep'],
  },
  aca: {
    id: 'aca',
    kind: 'platform',
    title: 'Container Apps',
    blurb: 'One image: the site, the API and the jobs.',
    body: [
      'The same image runs the API and this site, the nightly ingestion cron job and the KEDA-scaled analysis worker.',
      'One entry point serves both the web host and a CLI, so every write path also has a command-line verb. Secrets such as the Anthropic key come from Key Vault.',
    ],
    code: ['Api/Program.cs', 'Api/Cli'],
  },
}

export const LANES: Lane[] = [
  {
    id: 'search',
    title: 'When you search',
    subtitle: 'One LLM call, then everything runs on our own server.',
    columns: [['prompt'], ['plan'], ['dense', 'lexical'], ['fuse'], ['shortlist']],
    edges: [
      { from: 'prompt', to: 'plan' },
      { from: 'plan', to: 'dense' },
      { from: 'plan', to: 'lexical' },
      { from: 'dense', to: 'fuse' },
      { from: 'lexical', to: 'fuse' },
      { from: 'fuse', to: 'shortlist' },
    ],
    trace: [[0], [1, 2], [3, 4], [5]],
    traceLabel: 'Trace a search',
  },
  {
    id: 'analysis',
    title: 'When you pick a paper',
    subtitle: 'Opt-in, and you see the price first.',
    columns: [['estimate'], ['queue'], ['analyze'], ['ledger']],
    edges: [
      { from: 'estimate', to: 'queue' },
      { from: 'queue', to: 'analyze' },
      { from: 'analyze', to: 'ledger' },
    ],
    trace: [[0], [1], [2]],
    traceLabel: 'Trace an analysis',
  },
  {
    id: 'nightly',
    title: 'Every night',
    subtitle: 'Keeping the library fresh with nobody watching.',
    columns: [['arxiv'], ['ingest'], ['postgres'], ['embed'], ['snapshot']],
    edges: [
      { from: 'arxiv', to: 'ingest' },
      { from: 'ingest', to: 'postgres' },
      { from: 'postgres', to: 'embed' },
      { from: 'embed', to: 'snapshot' },
    ],
    trace: [[0], [1], [2], [3]],
    traceLabel: 'Trace a night',
  },
  {
    id: 'platform',
    title: 'Around all of it',
    subtitle: 'How it ships, who gets in, and how ranking stays honest.',
    columns: [['edge', 'eval'], ['auth', 'cicd'], ['aca']],
    edges: [
      { from: 'edge', to: 'auth' },
      { from: 'eval', to: 'cicd' },
      { from: 'auth', to: 'aca' },
      { from: 'cicd', to: 'aca' },
    ],
    trace: [[0, 1], [2, 3]],
    traceLabel: 'Trace a request',
  },
]

export const LANE_OF: Record<string, string> = Object.fromEntries(
  LANES.flatMap((lane) => lane.columns.flat().map((id) => [id, lane.id])),
)
