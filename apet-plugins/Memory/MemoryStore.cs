using Microsoft.Data.Sqlite;

namespace APet.Plugins.Memory;

/// <summary>一条记忆(时间戳为 Unix 秒,UTC)。</summary>
public readonly record struct MemoryEntry(long Id, string Content, long CreatedAt, long LastUsedAt, int UseCount);

/// <summary>
/// 单 Agent 的长期记忆存储:SQLite 文件 agents/{agentId}/memory.db,该 Agent 的全部会话共享。
/// 单连接 + 锁保护(SQLite 连接非线程安全;所有读写都在 _gate 内串行)。
/// 连接懒打开、首次使用时建表;操作失败以异常上抛,由工具层转成错误信息回传模型。
/// </summary>
public sealed class MemoryStore : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS memories (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            content TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            last_used_at INTEGER NOT NULL,
            use_count INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_mem_created ON memories(created_at);
        CREATE INDEX IF NOT EXISTS idx_mem_last_used ON memories(last_used_at);
        """;

    private readonly object _gate = new();
    private readonly string _path;
    private SqliteConnection? _conn;

    public MemoryStore(string agentId, string agentsRoot)
    {
        var dir = Path.Combine(agentsRoot, SafeDir(agentId));
        _path = Path.Combine(dir, "memory.db");
    }

    /// <summary>记录一条记忆(created=last_used=当前时间、use_count=0),返回新 id。</summary>
    public long Record(string content)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            EnsureOpen();
            using var tx = _conn!.BeginTransaction();
            using (var ins = _conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO memories (content, created_at, last_used_at) VALUES ($c, $created, $used);";
                ins.Parameters.AddWithValue("$c", content);
                ins.Parameters.AddWithValue("$created", now);
                ins.Parameters.AddWithValue("$used", now);
                ins.ExecuteNonQuery();
            }

            long id;
            using (var sel = _conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT last_insert_rowid();";
                id = (long)sel.ExecuteScalar()!;
            }

            tx.Commit();
            return id;
        }
    }

    /// <summary>最近 n 天活跃的记忆:窗口内创建、或窗口内被使用过(旧记忆);按最近活动倒序。</summary>
    public IReadOnlyList<MemoryEntry> Recent(int days)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)days * 86_400;
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                SELECT id, content, created_at, last_used_at, use_count FROM memories
                WHERE created_at >= $cutoff OR last_used_at >= $cutoff
                ORDER BY MAX(created_at, last_used_at) DESC;
                """;
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            return ReadAll(cmd);
        }
    }

    /// <summary>时间区间 [fromSec, toSec] 内创建或最后使用过的记忆;按创建时间正序。</summary>
    public IReadOnlyList<MemoryEntry> Range(long fromSec, long toSec)
    {
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = """
                SELECT id, content, created_at, last_used_at, use_count FROM memories
                WHERE (created_at BETWEEN $from AND $to) OR (last_used_at BETWEEN $from AND $to)
                ORDER BY created_at ASC;
                """;
            cmd.Parameters.AddWithValue("$from", fromSec);
            cmd.Parameters.AddWithValue("$to", toSec);
            return ReadAll(cmd);
        }
    }

    /// <summary>模糊搜索:content 包含任一关键词即命中(LIKE 子串,ASCII 不区分大小写);按最近使用倒序。</summary>
    public IReadOnlyList<MemoryEntry> Search(IReadOnlyList<string> keywords)
    {
        var where = string.Join(" OR ",
            Enumerable.Range(0, keywords.Count).Select(i => "content LIKE $k" + i + " ESCAPE '\\'"));
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = $"""
                SELECT id, content, created_at, last_used_at, use_count FROM memories
                WHERE {where}
                ORDER BY last_used_at DESC;
                """;
            for (var i = 0; i < keywords.Count; i++)
                cmd.Parameters.AddWithValue("$k" + i, LikePattern(keywords[i]));
            return ReadAll(cmd);
        }
    }

    /// <summary>使用一条记忆:use_count+1、last_used_at=当前时间;返回更新后的条目,不存在时 null。</summary>
    public MemoryEntry? Use(long id)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            EnsureOpen();
            using var up = _conn!.CreateCommand();
            up.CommandText = "UPDATE memories SET use_count = use_count + 1, last_used_at = $t WHERE id = $id;";
            up.Parameters.AddWithValue("$t", now);
            up.Parameters.AddWithValue("$id", id);
            if (up.ExecuteNonQuery() == 0)
                return null;

            using var sel = _conn.CreateCommand();
            sel.CommandText = "SELECT id, content, created_at, last_used_at, use_count FROM memories WHERE id = $id;";
            sel.Parameters.AddWithValue("$id", id);
            var list = ReadAll(sel);
            return list.Count > 0 ? list[0] : null;
        }
    }

    /// <summary>
    /// 编辑一条记忆:content 非空时更新文本并刷新 last_used_at(视为活跃);content 为空时删除该条。
    /// 返回 (是否找到, 更新后的条目;删除成功时为 null)。
    /// </summary>
    public (bool Found, MemoryEntry? Entry) Edit(long id, string? content)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_gate)
        {
            EnsureOpen();
            var conn = _conn!;
            if (string.IsNullOrWhiteSpace(content))
            {
                using var del = conn.CreateCommand();
                del.CommandText = "DELETE FROM memories WHERE id = $id;";
                del.Parameters.AddWithValue("$id", id);
                return (del.ExecuteNonQuery() > 0, null);
            }

            using var up = conn.CreateCommand();
            up.CommandText = "UPDATE memories SET content = $c, last_used_at = $t WHERE id = $id;";
            up.Parameters.AddWithValue("$c", content);
            up.Parameters.AddWithValue("$t", now);
            up.Parameters.AddWithValue("$id", id);
            if (up.ExecuteNonQuery() == 0)
                return (false, null);

            using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT id, content, created_at, last_used_at, use_count FROM memories WHERE id = $id;";
            sel.Parameters.AddWithValue("$id", id);
            var list = ReadAll(sel);
            return (true, list.Count > 0 ? list[0] : null);
        }
    }

    /// <summary>自动遗忘:删除 last_used_at 早于 now - keepDays 天的记忆,返回删除条数。</summary>
    public int Forget(int keepDays)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)keepDays * 86_400;
        lock (_gate)
        {
            EnsureOpen();
            using var cmd = _conn!.CreateCommand();
            cmd.CommandText = "DELETE FROM memories WHERE last_used_at < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _conn?.Dispose();
            _conn = null;
        }
    }

    private void EnsureOpen()
    {
        if (_conn is not null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _conn = new SqliteConnection($"Data Source={_path}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
    }

    private IReadOnlyList<MemoryEntry> ReadAll(SqliteCommand cmd)
    {
        var list = new List<MemoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new MemoryEntry(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt32(4)));
        return list;
    }

    /// <summary>转义 LIKE 通配符(%/_/\)并包成子串匹配模式。</summary>
    private static string LikePattern(string keyword) =>
        "%" + keyword.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private static string SafeDir(string id) =>
        string.Concat((id ?? "default").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
