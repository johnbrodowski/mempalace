using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MemPalace.Core;

public sealed class KnowledgeGraphService : IDisposable
{
    private readonly string _dbPath;

    public KnowledgeGraphService(AppConfig config)
        : this(config.KgDbPath) { }

    public KnowledgeGraphService(string dbPath)
    {
        _dbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        InitializeSchema();
    }

    // -------------------------------------------------------------------------
    // Infrastructure
    // -------------------------------------------------------------------------

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private void InitializeSchema()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
PRAGMA journal_mode=WAL;

CREATE TABLE IF NOT EXISTS entities (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    entity_type TEXT DEFAULT 'unknown',
    properties  TEXT DEFAULT '{}',
    created_at  TEXT DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_entities_name ON entities(name);

CREATE TABLE IF NOT EXISTS triples (
    id           TEXT PRIMARY KEY,
    subject      TEXT NOT NULL,
    predicate    TEXT NOT NULL,
    object       TEXT NOT NULL,
    valid_from   TEXT,
    valid_to     TEXT,
    confidence   REAL DEFAULT 1.0,
    source_ref   TEXT,
    source_file  TEXT,
    extracted_at TEXT DEFAULT (datetime('now'))
);
CREATE INDEX IF NOT EXISTS idx_triples_subject   ON triples(subject);
CREATE INDEX IF NOT EXISTS idx_triples_predicate ON triples(predicate);
";
        cmd.ExecuteNonQuery();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string EntityId(string name) =>
        name.ToLowerInvariant()
            .Replace(" ", "_")
            .Replace("'", "")
            .Replace("-", "_");

    private static string GenerateTripleId(string subject, string predicate, string obj)
    {
        var raw = $"{subject}|{predicate}|{obj}|{DateTime.UtcNow:o}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static KgFact ReadFact(SqliteDataReader r) => new()
    {
        Subject    = r.GetString(0),
        Predicate  = r.GetString(1),
        Object     = r.GetString(2),
        ValidFrom  = r.IsDBNull(3) ? null : r.GetString(3),
        ValidTo    = r.IsDBNull(4) ? null : r.GetString(4),
        Confidence = r.IsDBNull(5) ? 1.0  : r.GetDouble(5),
        SourceFile = r.IsDBNull(6) ? null : r.GetString(6),
        SourceRef  = r.IsDBNull(7) ? null : r.GetString(7),
        IsCurrent  = r.IsDBNull(4),
    };

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public string AddEntity(string name, string entityType = "unknown", Dictionary<string, object>? properties = null)
    {
        var id    = EntityId(name);
        var props = properties is not null
            ? JsonSerializer.Serialize(properties)
            : "{}";

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
INSERT OR IGNORE INTO entities(id, name, entity_type, properties)
VALUES(@id, @name, @type, @props)";
        cmd.Parameters.AddWithValue("@id",    id);
        cmd.Parameters.AddWithValue("@name",  name);
        cmd.Parameters.AddWithValue("@type",  entityType);
        cmd.Parameters.AddWithValue("@props", props);
        cmd.ExecuteNonQuery();
        return id;
    }

    public string AddTriple(
        string  subject,
        string  predicate,
        string  obj,
        string? validFrom  = null,
        string? validTo    = null,
        double  confidence = 1.0,
        string? sourceRef  = null,
        string? sourceFile = null)
    {
        var subjectId = EntityId(subject);
        var objectId  = EntityId(obj);

        // Ensure both ends exist as entities
        AddEntity(subject);
        AddEntity(obj);

        using var conn = Open();

        // Check for an existing active triple
        using (var chk = conn.CreateCommand())
        {
            chk.CommandText = @"
SELECT id FROM triples
WHERE subject = @s AND predicate = @p AND object = @o AND valid_to IS NULL
LIMIT 1";
            chk.Parameters.AddWithValue("@s", subjectId);
            chk.Parameters.AddWithValue("@p", predicate);
            chk.Parameters.AddWithValue("@o", objectId);
            var existing = chk.ExecuteScalar() as string;
            if (existing is not null) return existing;
        }

        var id = GenerateTripleId(subjectId, predicate, objectId);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
INSERT INTO triples(id, subject, predicate, object, valid_from, valid_to, confidence, source_ref, source_file)
VALUES(@id, @s, @p, @o, @vf, @vt, @conf, @sr, @sf)";
        cmd.Parameters.AddWithValue("@id",   id);
        cmd.Parameters.AddWithValue("@s",    subjectId);
        cmd.Parameters.AddWithValue("@p",    predicate);
        cmd.Parameters.AddWithValue("@o",    objectId);
        cmd.Parameters.AddWithValue("@vf",   (object?)validFrom ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@vt",   (object?)validTo   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@conf", confidence);
        cmd.Parameters.AddWithValue("@sr",   (object?)sourceRef  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sf",   (object?)sourceFile ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return id;
    }

    public void Invalidate(string subject, string predicate, string obj, string? ended = null)
    {
        ended ??= DateTime.UtcNow.ToString("yyyy-MM-dd");
        var subjectId = EntityId(subject);
        var objectId  = EntityId(obj);

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE triples
SET valid_to = @ended
WHERE subject = @s AND predicate = @p AND object = @o AND valid_to IS NULL";
        cmd.Parameters.AddWithValue("@ended", ended);
        cmd.Parameters.AddWithValue("@s",     subjectId);
        cmd.Parameters.AddWithValue("@p",     predicate);
        cmd.Parameters.AddWithValue("@o",     objectId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<KgFact> QueryEntity(string name, string? asOf = null, string direction = "both")
    {
        var entityId = EntityId(name);

        var dirClause = direction switch
        {
            "out"  => "subject = @id",
            "in"   => "object = @id",
            _      => "(subject = @id OR object = @id)",
        };

        var temporalClause = asOf is not null
            ? " AND (valid_from IS NULL OR valid_from <= @asOf) AND (valid_to IS NULL OR valid_to >= @asOf)"
            : "";

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT subject, predicate, object, valid_from, valid_to, confidence, source_file, source_ref
FROM triples
WHERE {dirClause}{temporalClause}";
        cmd.Parameters.AddWithValue("@id", entityId);
        if (asOf is not null) cmd.Parameters.AddWithValue("@asOf", asOf);

        var facts = new List<KgFact>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) facts.Add(ReadFact(reader));
        return facts;
    }

    public IReadOnlyList<KgFact> QueryRelationship(string predicate, string? asOf = null)
    {
        var temporalClause = asOf is not null
            ? " AND (valid_from IS NULL OR valid_from <= @asOf) AND (valid_to IS NULL OR valid_to >= @asOf)"
            : "";

        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = $@"
SELECT subject, predicate, object, valid_from, valid_to, confidence, source_file, source_ref
FROM triples
WHERE predicate = @pred{temporalClause}";
        cmd.Parameters.AddWithValue("@pred", predicate);
        if (asOf is not null) cmd.Parameters.AddWithValue("@asOf", asOf);

        var facts = new List<KgFact>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) facts.Add(ReadFact(reader));
        return facts;
    }

    public IReadOnlyList<KgFact> Timeline(string? entityName = null)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();

        if (entityName is not null)
        {
            var entityId = EntityId(entityName);
            cmd.CommandText = @"
SELECT subject, predicate, object, valid_from, valid_to, confidence, source_file, source_ref
FROM triples
WHERE subject = @id OR object = @id
ORDER BY COALESCE(valid_from, extracted_at) DESC";
            cmd.Parameters.AddWithValue("@id", entityId);
        }
        else
        {
            cmd.CommandText = @"
SELECT subject, predicate, object, valid_from, valid_to, confidence, source_file, source_ref
FROM triples
ORDER BY COALESCE(valid_from, extracted_at) DESC";
        }

        var facts = new List<KgFact>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) facts.Add(ReadFact(reader));
        return facts;
    }

    public KgStats Stats()
    {
        using var conn = Open();

        int entityCount, tripleCount, activeCount;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM entities";
            entityCount = Convert.ToInt32(cmd.ExecuteScalar()!);
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM triples";
            tripleCount = Convert.ToInt32(cmd.ExecuteScalar()!);
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM triples WHERE valid_to IS NULL";
            activeCount = Convert.ToInt32(cmd.ExecuteScalar()!);
        }

        var typeCounts = new Dictionary<string, int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT entity_type, COUNT(*) FROM entities GROUP BY entity_type";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                typeCounts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return new KgStats
        {
            EntityCount       = entityCount,
            TripleCount       = tripleCount,
            ActiveTripleCount = activeCount,
            TypeCounts        = typeCounts,
        };
    }

    public void SeedFromEntityFacts(Dictionary<string, object> entityFacts)
    {
        foreach (var (key, value) in entityFacts)
        {
            // Key format: "type:name" (e.g. "person:Alice") or just a plain name
            var colonIdx = key.IndexOf(':');
            string entityType, entityName;
            if (colonIdx >= 0)
            {
                entityType = key[..colonIdx].Trim();
                entityName = key[(colonIdx + 1)..].Trim();
            }
            else
            {
                entityType = "unknown";
                entityName = key.Trim();
            }

            AddEntity(entityName, entityType);

            // Value can be a Dictionary<string,object> (predicate→object) or a list of strings
            if (value is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in je.EnumerateObject())
                        AddTriple(entityName, prop.Name, prop.Value.ToString());
                }
                else if (je.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in je.EnumerateArray())
                        AddTriple(entityName, "related_to", item.ToString());
                }
            }
            else if (value is Dictionary<string, object> dict)
            {
                foreach (var (predicate, obj) in dict)
                    AddTriple(entityName, predicate, obj?.ToString() ?? "");
            }
            else if (value is IEnumerable<string> list)
            {
                foreach (var item in list)
                    AddTriple(entityName, "related_to", item);
            }
        }
    }

    public KgEntity? GetEntity(string name)
    {
        var id = EntityId(name);
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, entity_type, properties, created_at FROM entities WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new KgEntity
        {
            Id             = r.GetString(0),
            Name           = r.GetString(1),
            EntityType     = r.GetString(2),
            PropertiesJson = r.GetString(3),
            CreatedAt      = r.GetString(4),
        };
    }

    public IReadOnlyList<KgEntity> GetAllEntities()
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, entity_type, properties, created_at FROM entities ORDER BY name";
        var entities = new List<KgEntity>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            entities.Add(new KgEntity
            {
                Id             = r.GetString(0),
                Name           = r.GetString(1),
                EntityType     = r.GetString(2),
                PropertiesJson = r.GetString(3),
                CreatedAt      = r.GetString(4),
            });
        return entities;
    }

    public KgTriple? GetTriple(string id)
    {
        using var conn = Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
SELECT id, subject, predicate, object, valid_from, valid_to, confidence, source_ref, source_file, extracted_at
FROM triples WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new KgTriple
        {
            Id           = r.GetString(0),
            Subject      = r.GetString(1),
            Predicate    = r.GetString(2),
            Object       = r.GetString(3),
            ValidFrom    = r.IsDBNull(4) ? null : r.GetString(4),
            ValidTo      = r.IsDBNull(5) ? null : r.GetString(5),
            Confidence   = r.IsDBNull(6) ? 1.0  : r.GetDouble(6),
            SourceRef    = r.IsDBNull(7) ? null : r.GetString(7),
            SourceFile   = r.IsDBNull(8) ? null : r.GetString(8),
            ExtractedAt  = r.GetString(9),
        };
    }

    public void Dispose() { /* Connection objects are opened/closed per call; nothing to dispose. */ }
}
