# MemPalace C# Port

A full C#/.NET 8 port of MemPalace's CLI and MCP server — with no external vector database required.

## Why C# instead of Python?

The original Python port uses **ChromaDB** as its vector store, which embeds every chunk through the
`all-MiniLM-L6-v2` ONNX neural model before writing it to an on-disk HNSW index. That architecture
brings real costs:

| Concern | Python + ChromaDB | C# + SQLite FTS5 |
|---------|-------------------|------------------|
| **Cold start** | Model load + ONNX warm-up (~3 s) | <100 ms (BM25-only mode) |
| **Index writes** | Neural embedding per chunk → HNSW insert | SQL `INSERT` (+ optional async embed) |
| **Search** | Cosine similarity scan | BM25 + optional cosine RRF hybrid |
| **Dependencies** | chromadb, onnxruntime, numpy, … (~500 MB) | Microsoft.Data.Sqlite + FastBertTokenizer (~4 MB) |
| **Portability** | Requires Python ≥ 3.9, pip, venv | Single self-contained binary |
| **File locking** | ChromaDB keeps `data_level0.bin` locked; `shutil.rmtree` fails on Windows | SQLite WAL; trivially closed |

### Measured test-suite speed (same machine, model cached)

```
Python  pytest   20 tests   6.41 s   (2 fail: Windows file-lock in teardown)
C#      xUnit    33 tests   0.80 s   (0 failures)
```

**~8× faster end-to-end**, and that gap widens with corpus size: ChromaDB's ONNX inference time
scales with the number of chunks being embedded, while SQLite BM25 is a pure inverted-index lookup
that scales with result-set size rather than corpus size.

### Semantic search — hybrid BM25 + cosine RRF

The C# port includes full semantic search using the same `all-MiniLM-L6-v2` model as ChromaDB,
implemented without ChromaDB:

- **Tokenizer:** [FastBertTokenizer](https://github.com/georg-jung/FastBertTokenizer) — a
  zero-dependency WordPiece tokenizer; encodes 1 GB of text in ~2 s; returns `Memory<long>` that
  feeds directly into ONNX with no copying
- **Inference:** `Microsoft.ML.OnnxRuntime` — thread-safe `InferenceSession.Run`; mean-pools
  `last_hidden_state [batch, seq, 384]` with L2 normalisation
- **Storage:** a `chunk_embeddings` table (`chunk_id → BLOB`, 1 536 bytes per row) alongside the
  existing chunks table, with `ON DELETE CASCADE` so embeddings are pruned automatically
- **Retrieval:** [Reciprocal Rank Fusion](https://dl.acm.org/doi/10.1145/1571941.1572114) (RRF,
  k = 60) fuses a BM25 leg and a cosine-similarity leg; the top-N fused IDs are then hydrated from
  the DB in a single `WHERE id IN (…)` query

When the model is absent or `--no-embed` is passed, the system silently falls back to BM25-only —
zero configuration required.

The model (~90 MB) is downloaded automatically from HuggingFace on first use:

```
~/.mempalace/models/model.onnx
~/.mempalace/models/vocab.txt
```

## Concepts

| Term | Meaning |
|------|---------|
| **domain** | Top-level partition — a project, person, or subject area |
| **topic** | Sub-topic within a domain |
| **category** | Classification of a chunk (e.g. `cat_facts`, `cat_decisions`, `cat_events`) |
| **chunk** | A stored text segment (800-char slice of a source file or conversation) |
| **ref** | A source reference linking a knowledge-graph triple back to its origin chunk |
| **link** | A cross-domain topical connection (topic that appears in 2+ domains) |
| **log** | An agent's personal operational log |

## Architecture

- **Storage:** SQLite (`~/.mempalace/palace.db`) with FTS5 full-text search (BM25 ranking) and a
  `chunk_embeddings` side-table for vector search — no external vector database required
- **Knowledge graph:** separate SQLite file (`~/.mempalace/knowledge_graph.db`) with temporal entity-relation triples
- **Search:** hybrid BM25 + cosine RRF when the embedding model is present; pure BM25 otherwise
- **Semantic model:** `all-MiniLM-L6-v2` ONNX, loaded via FastBertTokenizer + OnnxRuntime
- **MCP server:** JSONRPC 2.0 over stdin/stdout with 18 tools

## Commands

```
init   [path]                 Initialize store directories
mine   <path> [flags]         Index files into the store
  --mode   projects|convos|general   (default: projects)
  --domain <name>             Assign a domain name
  --limit  <n>                Max files to process
  --dry-run                   Preview without writing
search <query> [flags]        Hybrid BM25 + semantic search (BM25-only if no model)
  --domain <name>             Filter by domain
  --topic  <name>             Filter by topic
  --limit  <n>                Max results (default: 10)
--no-embed                    Skip loading the embedding model (BM25-only, faster cold start)
status                        Show store stats (chunks, domains, topics, KG)
wake-up [--domain <name>]     Print 4-layer memory context (L0–L3)
compress <text|file>          AAAK-compress text (~30x token reduction)
split  <dir|file> [flags]     Split multi-session transcripts
  --output <dir>              Output directory
  --dry-run                   Preview only
repair                        Rebuild FTS5 search index
onboard [dir] [--no-detect]   Run first-time setup wizard
mcp                           Start MCP server (stdin/stdout)
help                          Show help
```

## Quick Start

```bash
# Initialize
dotnet run --project csharp/src/MemPalace -- init

# Index a project directory (downloads embedding model on first run)
dotnet run --project csharp/src/MemPalace -- mine /path/to/project --domain my-project

# Index without downloading the model (BM25-only, instant start)
dotnet run --project csharp/src/MemPalace -- mine /path/to/project --domain my-project --no-embed

# Search (hybrid if model present, BM25 otherwise)
dotnet run --project csharp/src/MemPalace -- search "authentication decisions"
dotnet run --project csharp/src/MemPalace -- search "deployment" --domain my-project --topic technical

# Wake-up context for an AI session
dotnet run --project csharp/src/MemPalace -- wake-up

# Compress text with the AAAK dialect
dotnet run --project csharp/src/MemPalace -- compress "We decided to use Postgres because of scale"

# Start MCP server
dotnet run --project csharp/src/MemPalace -- mcp
```

## MCP Tools

| Tool | Description |
|------|-------------|
| `mempalace_status` | Store overview and stats |
| `mempalace_list_domains` | List all domains |
| `mempalace_list_topics` | List topics (optionally filtered by domain) |
| `mempalace_search` | Hybrid BM25 + semantic search with optional domain/topic filters |
| `mempalace_check_duplicate` | Check if content is already indexed (cosine or Jaccard) |
| `mempalace_add_chunk` | Add a text chunk to the store |
| `mempalace_delete_chunk` | Delete a chunk by ID |
| `mempalace_kg_query` | Query the knowledge graph for an entity |
| `mempalace_kg_add` | Add a triple (fact) to the knowledge graph |
| `mempalace_kg_invalidate` | Mark a triple as no longer valid |
| `mempalace_kg_timeline` | Get a chronological fact history |
| `mempalace_kg_stats` | Knowledge graph statistics |
| `mempalace_graph_stats` | Palace graph statistics (nodes, edges, domains) |
| `mempalace_traverse` | BFS traverse the topic graph from a starting topic |
| `mempalace_find_links` | Find topics that connect multiple domains |
| `mempalace_log_write` | Write an entry to an agent's log |
| `mempalace_log_read` | Read recent entries from an agent's log |
| `mempalace_get_aaak_spec` | Return the full AAAK compression dialect specification |

## Services

| Service | Purpose |
|---------|---------|
| `DatabaseService` | SQLite + FTS5 store; all chunk CRUD, BM25 search, and embedding BLOB storage |
| `EmbeddingService` | 384-dim sentence embeddings via all-MiniLM-L6-v2 ONNX + FastBertTokenizer; RRF hybrid search |
| `ModelDownloader` | Downloads `model.onnx` + `vocab.txt` from HuggingFace on first use |
| `KnowledgeGraphService` | Temporal entity-relation triples (separate DB) |
| `MinerService` | File indexing with gitignore support, 800-char chunking, best-effort embedding |
| `ConvoMinerService` | Conversation indexing with exchange-pair chunking |
| `NormalizeService` | Normalizes 5 chat formats to standard transcript |
| `RoomDetectorService` | Keyword-scoring topic/category detection |
| `GeneralExtractorService` | Pattern-based extraction of decisions, preferences, milestones, problems, emotions |
| `EntityDetectorService` | Two-pass person/project detection from prose |
| `EntityRegistryService` | Persistent entity registry with disambiguation |
| `DialectService` | AAAK compression dialect (emotion codes, flags, entity codes) |
| `PalaceGraphService` | BFS graph traversal, cross-domain link detection |
| `LayerService` | 4-layer context stack (L0 identity → L1 summary → L2 on-demand → L3 search) |
| `OnboardingService` | Interactive first-run setup wizard |
| `SplitMegaFilesService` | Splits concatenated multi-session transcripts |
| `McpServerService` | JSONRPC 2.0 MCP server over stdin/stdout (18 tools) |

## Testing

Three test projects are included:

### Unit tests (xUnit)
```bash
dotnet test src/MemPalace.Tests
```
Covers config, miner (12 gitignore cases), convo-miner, and normalizer. All tests are isolated
via env-var overrides — nothing writes to `~/.mempalace`.

### Integration tests (real pipeline, dogfooded against the C# source)
```bash
dotnet run --project src/MemPalace.IntegrationTests
```
33 end-to-end tests across init, mine, FTS5 search, status, knowledge graph (11 assertions),
normalize, AAAK compress, conversation mining, KG seed, and palace graph. Completes in under 1 s.
No embedding model required — runs in BM25-only mode.

### Benchmarks
```bash
dotnet run --project src/MemPalace.Benchmarks -- membench   <data_dir>
dotnet run --project src/MemPalace.Benchmarks -- convomem
dotnet run --project src/MemPalace.Benchmarks -- locomo     <data_file.json>
dotnet run --project src/MemPalace.Benchmarks -- longmemeval <data_file.json>
```
Ports of the four Python benchmark scripts (MemBench, ConvoMem, LoCoMo, LongMemEval) using hybrid
BM25 + cosine RRF retrieval in place of ChromaDB.

## Data Files

| Path | Contents |
|------|----------|
| `~/.mempalace/palace.db` | Chunks table + FTS5 index + chunk_embeddings BLOB table |
| `~/.mempalace/knowledge_graph.db` | KG entities and triples |
| `~/.mempalace/models/model.onnx` | all-MiniLM-L6-v2 ONNX weights (~87 MB, auto-downloaded) |
| `~/.mempalace/models/vocab.txt` | WordPiece vocabulary (~230 KB, auto-downloaded) |
| `~/.mempalace/entity_registry.json` | Known people and projects |
| `~/.mempalace/identity.txt` | L0 identity context (user-created) |
| `~/.mempalace/config.json` | Store configuration |
| `~/.mempalace/aaak_entities.md` | AAAK entity code table (generated by onboard) |
| `~/.mempalace/critical_facts.md` | Critical facts summary (generated by onboard) |
