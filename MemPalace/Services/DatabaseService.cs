using Microsoft.Data.Sqlite;

namespace MemPalace.Core;

public sealed class DatabaseService
{
    private readonly string _dbPath;

    public DatabaseService(AppConfig config)
    {
        _dbPath = config.DbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        InitializeSchema();
    }

    private SqliteConnection Open() =>
        new($"Data Source={_dbPath}");

    /// <summary>Enable FK enforcement on an already-opened connection (required for ON DELETE CASCADE).</summary>
    private static void EnableForeignKeys(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON";
        cmd.ExecuteNonQuery();
    }

    private void InitializeSchema()
    {
        using var conn = Open();
        conn.Open();
        EnableForeignKeys(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;

CREATE TABLE IF NOT EXISTS chunks (
    id         TEXT PRIMARY KEY,
    domain     TEXT NOT NULL DEFAULT '',
    topic      TEXT NOT NULL DEFAULT 'general',
    category   TEXT NOT NULL DEFAULT '',
    source_file TEXT NOT NULL DEFAULT '',
    content    TEXT NOT NULL,
    chunk_index INTEGER NOT NULL DEFAULT 0,
    added_by   TEXT NOT NULL DEFAULT 'mempalace',
    filed_at   TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_chunks_domain ON chunks(domain);
CREATE INDEX IF NOT EXISTS idx_chunks_topic ON chunks(topic);
CREATE INDEX IF NOT EXISTS idx_chunks_source ON chunks(source_file);

-- Standalone FTS5 table (stores chunk content redundantly for simplicity and robust rebuild)
CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
    content,
    domain,
    topic,
    category,
    source_file,
    chunk_id UNINDEXED,
    tokenize='unicode61'
);

CREATE TRIGGER IF NOT EXISTS chunks_ai AFTER INSERT ON chunks BEGIN
    INSERT INTO chunks_fts(content, domain, topic, category, source_file, chunk_id)
    VALUES (new.content, new.domain, new.topic, new.category, new.source_file, new.id);
END;

CREATE TRIGGER IF NOT EXISTS chunks_ad AFTER DELETE ON chunks BEGIN
    DELETE FROM chunks_fts WHERE chunk_id = old.id;
END;

-- Vector embeddings table (384-dim float32 stored as raw bytes = 1,536 bytes per row)
CREATE TABLE IF NOT EXISTS chunk_embeddings (
    chunk_id  TEXT PRIMARY KEY REFERENCES chunks(id) ON DELETE CASCADE,
    embedding BLOB NOT NULL
);
";
        cmd.ExecuteNonQuery();
    }

    public void AddChunk(ChunkRecord doc)
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO chunks(id, domain, topic, category, source_file, content, chunk_index, added_by, filed_at)
VALUES(@id, @domain, @topic, @category, @src, @content, @chunk, @by, @at)";
        cmd.Parameters.AddWithValue("@id", doc.Id);
        cmd.Parameters.AddWithValue("@domain", doc.Domain);
        cmd.Parameters.AddWithValue("@topic", doc.Topic);
        cmd.Parameters.AddWithValue("@category", doc.Category);
        cmd.Parameters.AddWithValue("@src", doc.SourceFile);
        cmd.Parameters.AddWithValue("@content", doc.Content);
        cmd.Parameters.AddWithValue("@chunk", doc.ChunkIndex);
        cmd.Parameters.AddWithValue("@by", doc.AddedBy);
        cmd.Parameters.AddWithValue("@at", doc.FiledAt);
        cmd.ExecuteNonQuery();
    }

    public bool ChunkExists(string sourceFile)
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE source_file = @src LIMIT 1";
        cmd.Parameters.AddWithValue("@src", sourceFile);
        return Convert.ToInt64(cmd.ExecuteScalar()!) > 0;
    }

    public IReadOnlyList<ChunkRecord> GetAllChunks(string? domain = null, string? topic = null)
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (domain is not null) { where.Add("domain = @domain"); cmd.Parameters.AddWithValue("@domain", domain); }
        if (topic is not null) { where.Add("topic = @topic"); cmd.Parameters.AddWithValue("@topic", topic); }
        cmd.CommandText = "SELECT id,domain,topic,category,source_file,content,chunk_index,added_by,filed_at FROM chunks"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY filed_at DESC";
        return ReadChunks(cmd);
    }

    public IReadOnlyList<SearchResult> SearchFts5(string query, string? domain = null, string? topic = null, int limit = 10)
    {
        // Escape FTS5 query: wrap in quotes if it contains special chars
        var ftsQuery = EscapeFts5Query(query);
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Standalone FTS5: filter on FTS columns directly
        var domainFilter = domain is not null ? " AND domain = @domain" : "";
        var topicFilter = topic is not null ? " AND topic = @topic" : "";
        cmd.CommandText = $@"
SELECT chunk_id, domain, topic, category, source_file, content, bm25(chunks_fts) AS rank
FROM chunks_fts
WHERE chunks_fts MATCH @q{domainFilter}{topicFilter}
ORDER BY rank
LIMIT @limit";
        cmd.Parameters.AddWithValue("@q", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (domain is not null) cmd.Parameters.AddWithValue("@domain", domain);
        if (topic is not null) cmd.Parameters.AddWithValue("@topic", topic);

        var results = new List<SearchResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var content = reader.GetString(5);
            results.Add(new SearchResult
            {
                Id = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Domain = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Topic = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Category = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Title = reader.IsDBNull(4) ? "unknown" : Path.GetFileName(reader.GetString(4)),
                Snippet = content.Length > 200 ? content[..200] + "…" : content,
                Score = -reader.GetDouble(6) // BM25 returns negative; negate for display
            });
        }
        return results;
    }

    public void DeleteChunk(string id)
    {
        using var conn = Open();
        conn.Open();
        EnableForeignKeys(conn);   // ensures ON DELETE CASCADE removes the embedding row too
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chunks WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, Dictionary<string, int>> GetTaxonomy()
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT domain, topic, COUNT(*) FROM chunks GROUP BY domain, topic ORDER BY domain, topic";
        var taxonomy = new Dictionary<string, Dictionary<string, int>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var d = reader.GetString(0);
            var t = reader.GetString(1);
            var c = reader.GetInt32(2);
            if (!taxonomy.ContainsKey(d)) taxonomy[d] = new();
            taxonomy[d][t] = c;
        }
        return taxonomy;
    }

    public int GetChunkCount(string? domain = null)
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();
        if (domain is not null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE domain = @domain";
            cmd.Parameters.AddWithValue("@domain", domain);
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM chunks";
        }
        return Convert.ToInt32(cmd.ExecuteScalar()!);
    }

    /// <summary>
    /// Returns chunks that do not yet have a stored embedding, optionally filtered
    /// by domain and/or topic. Used by the retroactive 'embed' command.
    /// </summary>
    public IReadOnlyList<ChunkRecord> GetChunksWithoutEmbeddings(
        string? domain = null, string? topic = null)
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();

        var where = new List<string> { "c.id NOT IN (SELECT chunk_id FROM chunk_embeddings)" };
        if (domain is not null) { where.Add("c.domain = @domain"); cmd.Parameters.AddWithValue("@domain", domain); }
        if (topic  is not null) { where.Add("c.topic  = @topic");  cmd.Parameters.AddWithValue("@topic",  topic); }

        cmd.CommandText =
            $"SELECT id, domain, topic, category, source_file, content, chunk_index, added_by, filed_at " +
            $"FROM chunks c WHERE {string.Join(" AND ", where)} " +
            $"ORDER BY domain, topic, source_file, chunk_index";

        return ReadChunks(cmd);
    }

    public IReadOnlyList<string> GetDomains()
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT domain FROM chunks ORDER BY domain";
        var domains = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) domains.Add(reader.GetString(0));
        return domains;
    }

    public IReadOnlyList<string> GetTopics(string? domain = null)
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();
        if (domain is not null)
        {
            cmd.CommandText = "SELECT DISTINCT topic FROM chunks WHERE domain = @domain ORDER BY topic";
            cmd.Parameters.AddWithValue("@domain", domain);
        }
        else
        {
            cmd.CommandText = "SELECT DISTINCT topic FROM chunks ORDER BY topic";
        }
        var topics = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) topics.Add(reader.GetString(0));
        return topics;
    }

    public void RebuildFts()
    {
        // Standalone FTS5: delete all rows and reinsert from the chunks table
        using var conn = Open();
        conn.Open();
        using var tx = conn.BeginTransaction();
        using var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM chunks_fts";
        del.ExecuteNonQuery();

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = @"
INSERT INTO chunks_fts(content, domain, topic, category, source_file, chunk_id)
SELECT content, domain, topic, category, source_file, id FROM chunks";
        ins.ExecuteNonQuery();

        tx.Commit();
    }

    public IReadOnlyList<ChunkRecord> GetRecentChunks(int limit = 15, string? domain = null)
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();
        if (domain is not null)
        {
            cmd.CommandText = "SELECT id,domain,topic,category,source_file,content,chunk_index,added_by,filed_at FROM chunks WHERE domain = @domain ORDER BY filed_at DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@domain", domain);
        }
        else
        {
            cmd.CommandText = "SELECT id,domain,topic,category,source_file,content,chunk_index,added_by,filed_at FROM chunks ORDER BY filed_at DESC LIMIT @limit";
        }
        cmd.Parameters.AddWithValue("@limit", limit);
        return ReadChunks(cmd);
    }

    // ── Embedding storage ─────────────────────────────────────────────────────

    /// <summary>Store (or replace) a 384-dim float32 embedding for a chunk.</summary>
    public void UpsertEmbedding(string chunkId, float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        using var conn = Open();
        conn.Open();
        EnableForeignKeys(conn);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO chunk_embeddings(chunk_id, embedding) VALUES(@id, @emb)";
        cmd.Parameters.AddWithValue("@id", chunkId);
        cmd.Parameters.AddWithValue("@emb", bytes);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Retrieve a stored embedding by chunk ID, or null if absent.</summary>
    public float[]? GetEmbedding(string chunkId)
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT embedding FROM chunk_embeddings WHERE chunk_id = @id";
        cmd.Parameters.AddWithValue("@id", chunkId);
        var raw = cmd.ExecuteScalar();
        if (raw is null or DBNull) return null;
        var bytes = (byte[])raw;
        var vec = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vec, 0, bytes.Length);
        return vec;
    }

    /// <summary>Total number of embeddings stored (used to decide whether to do hybrid search).</summary>
    public int GetEmbeddingCount()
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunk_embeddings";
        return Convert.ToInt32(cmd.ExecuteScalar()!);
    }

    /// <summary>
    /// Fetch all (chunkId, embedding) pairs, optionally filtered to chunks matching
    /// a given domain and/or topic. Used for brute-force cosine similarity search.
    /// </summary>
    public IReadOnlyList<(string ChunkId, float[] Embedding)> GetAllEmbeddings(
        string? domain = null, string? topic = null)
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();

        if (domain is not null || topic is not null)
        {
            var filters = new List<string>();
            if (domain is not null) { filters.Add("c.domain = @domain"); cmd.Parameters.AddWithValue("@domain", domain); }
            if (topic  is not null) { filters.Add("c.topic  = @topic");  cmd.Parameters.AddWithValue("@topic",  topic);  }
            cmd.CommandText =
                $"SELECT e.chunk_id, e.embedding FROM chunk_embeddings e " +
                $"JOIN chunks c ON e.chunk_id = c.id WHERE {string.Join(" AND ", filters)}";
        }
        else
        {
            cmd.CommandText = "SELECT chunk_id, embedding FROM chunk_embeddings";
        }

        var results = new List<(string, float[])>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id    = reader.GetString(0);
            var bytes = (byte[])reader.GetValue(1);
            var vec   = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, vec, 0, bytes.Length);
            results.Add((id, vec));
        }
        return results;
    }

    /// <summary>
    /// Fetch stored embeddings joined with chunk metadata in a single query.
    /// Returns one row per embedding, ordered by domain then topic then source_file.
    /// </summary>
    public IReadOnlyList<EmbeddingRow> GetEmbeddingsWithMeta(
        string? domain = null, string? topic = null)
    {
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (domain is not null) { where.Add("c.domain = @domain"); cmd.Parameters.AddWithValue("@domain", domain); }
        if (topic  is not null) { where.Add("c.topic  = @topic");  cmd.Parameters.AddWithValue("@topic",  topic); }
        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        cmd.CommandText =
            $"SELECT e.chunk_id, e.embedding, c.domain, c.topic, c.source_file, c.content " +
            $"FROM chunk_embeddings e " +
            $"JOIN chunks c ON e.chunk_id = c.id " +
            $"{whereClause} " +
            $"ORDER BY c.domain, c.topic, c.source_file";

        var results = new List<EmbeddingRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var bytes   = (byte[])reader.GetValue(1);
            var vec     = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, vec, 0, bytes.Length);
            var content = reader.GetString(5);
            results.Add(new EmbeddingRow(
                ChunkId : reader.GetString(0),
                Embedding: vec,
                Domain  : reader.GetString(2),
                Topic   : reader.GetString(3),
                Title   : reader.IsDBNull(4) ? "unknown" : Path.GetFileName(reader.GetString(4)),
                Snippet : content.Length > 120 ? content[..120] + "…" : content
            ));
        }
        return results;
    }

    /// <summary>Fetch full SearchResult rows for a list of chunk IDs (hydration after cosine ranking).</summary>
    public IReadOnlyList<SearchResult> GetChunksByIds(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return Array.Empty<SearchResult>();
        using var conn = Open();
        conn.Open();
        using var cmd = conn.CreateCommand();
        var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
        cmd.CommandText =
            $"SELECT id, domain, topic, category, source_file, content " +
            $"FROM chunks WHERE id IN ({placeholders})";
        for (int i = 0; i < ids.Count; i++)
            cmd.Parameters.AddWithValue($"@id{i}", ids[i]);

        var results = new List<SearchResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var content = reader.GetString(5);
            results.Add(new SearchResult
            {
                Id       = reader.GetString(0),
                Domain   = reader.GetString(1),
                Topic    = reader.GetString(2),
                Category = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Title    = reader.IsDBNull(4) ? "unknown" : Path.GetFileName(reader.GetString(4)),
                Snippet  = content.Length > 200 ? content[..200] + "…" : content,
                Score    = 0  // caller overwrites with RRF score
            });
        }
        return results;
    }

    private static IReadOnlyList<ChunkRecord> ReadChunks(SqliteCommand cmd)
    {
        var docs = new List<ChunkRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            docs.Add(new ChunkRecord
            {
                Id = reader.GetString(0),
                Domain = reader.GetString(1),
                Topic = reader.GetString(2),
                Category = reader.IsDBNull(3) ? "" : reader.GetString(3),
                SourceFile = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Content = reader.GetString(5),
                ChunkIndex = reader.GetInt32(6),
                AddedBy = reader.IsDBNull(7) ? "mempalace" : reader.GetString(7),
                FiledAt = reader.IsDBNull(8) ? "" : reader.GetString(8),
            });
        }
        return docs;
    }

    private static string EscapeFts5Query(string query)
    {
        // If query contains FTS5 special chars, wrap each token in double quotes
        var tokens = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return "\"\"";
        var escaped = tokens.Select(t =>
        {
            // Remove characters that break FTS5
            var clean = t.Replace("\"", "").Replace("*", "").Replace("^", "").Trim();
            return clean.Length > 0 ? $"\"{clean}\"" : null;
        }).Where(t => t is not null);
        var result = string.Join(" ", escaped);
        return string.IsNullOrWhiteSpace(result) ? "\"\"" : result;
    }
}
