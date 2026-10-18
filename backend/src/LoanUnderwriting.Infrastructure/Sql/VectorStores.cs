using System.Globalization;
using System.Text.Json;
using LoanUnderwriting.Core;
using Microsoft.Data.SqlClient;

namespace LoanUnderwriting.Infrastructure.Sql;

internal static class VectorSql
{
    public static string ToJson(float[] v) => "[" + string.Join(",", v.Select(x => x.ToString("R", CultureInfo.InvariantCulture))) + "]";
}

/// <summary>SQL Server 2025 / Azure SQL native VECTOR column with exact cosine search, always filtered to one case.</summary>
public sealed class NativeVectorStore(SqlDb db, int dimensions) : IVectorStore
{
    public async Task ReplaceAsync(Guid caseId, IReadOnlyList<ChunkEmbedding> chunks, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM document_chunks WHERE case_id=@id";
            del.Parameters.AddWithValue("@id", caseId);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var c in chunks)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = $@"INSERT INTO document_chunks (chunk_id, case_id, page, chunk_index, text, content_hash, embedding)
                                 VALUES (@cid,@case,@page,@idx,@text,@hash,CAST(@emb AS VECTOR({dimensions})))";
            ins.Parameters.AddWithValue("@cid", c.ChunkId);
            ins.Parameters.AddWithValue("@case", caseId);
            ins.Parameters.AddWithValue("@page", c.Page);
            ins.Parameters.AddWithValue("@idx", c.Index);
            ins.Parameters.AddWithValue("@text", c.Text);
            ins.Parameters.AddWithValue("@hash", c.ContentHash);
            ins.Parameters.AddWithValue("@emb", VectorSql.ToJson(c.Embedding));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchAsync(Guid caseId, float[] query, int k, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        // The WHERE case_id filter is part of the query itself, not of the prompt.
        c.CommandText = $@"SELECT TOP (@k) chunk_id, page, text,
                                  VECTOR_DISTANCE('cosine', embedding, CAST(@q AS VECTOR({dimensions}))) AS distance
                           FROM document_chunks WHERE case_id=@case ORDER BY distance";
        c.Parameters.AddWithValue("@k", k);
        c.Parameters.AddWithValue("@case", caseId);
        c.Parameters.AddWithValue("@q", VectorSql.ToJson(query));
        var list = new List<ScoredChunk>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ScoredChunk(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetDouble(3)));
        return list;
    }

    public async Task DeleteCaseAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "DELETE FROM document_chunks WHERE case_id=@id";
        c.Parameters.AddWithValue("@id", caseId);
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM document_chunks WHERE case_id=@id";
        c.Parameters.AddWithValue("@id", caseId);
        return (int)(await c.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>Development fallback for SQL Server versions without the VECTOR type: JSON embeddings, cosine computed in C#.</summary>
public sealed class JsonVectorStore(SqlDb db) : IVectorStore
{
    public async Task ReplaceAsync(Guid caseId, IReadOnlyList<ChunkEmbedding> chunks, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM document_chunks WHERE case_id=@id";
            del.Parameters.AddWithValue("@id", caseId);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var c in chunks)
        {
            await using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = @"INSERT INTO document_chunks (chunk_id, case_id, page, chunk_index, text, content_hash, embedding)
                                VALUES (@cid,@case,@page,@idx,@text,@hash,@emb)";
            ins.Parameters.AddWithValue("@cid", c.ChunkId);
            ins.Parameters.AddWithValue("@case", caseId);
            ins.Parameters.AddWithValue("@page", c.Page);
            ins.Parameters.AddWithValue("@idx", c.Index);
            ins.Parameters.AddWithValue("@text", c.Text);
            ins.Parameters.AddWithValue("@hash", c.ContentHash);
            ins.Parameters.AddWithValue("@emb", VectorSql.ToJson(c.Embedding));
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchAsync(Guid caseId, float[] query, int k, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "SELECT chunk_id, page, text, embedding FROM document_chunks WHERE case_id=@case";
        c.Parameters.AddWithValue("@case", caseId);
        var rows = new List<ScoredChunk>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var v = JsonSerializer.Deserialize<float[]>(r.GetString(3))!;
            rows.Add(new ScoredChunk(r.GetString(0), r.GetInt32(1), r.GetString(2), CosineDistance(query, v)));
        }
        return rows.OrderBy(x => x.Distance).Take(k).ToList();
    }

    private static double CosineDistance(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 1 : 1 - dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public async Task DeleteCaseAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "DELETE FROM document_chunks WHERE case_id=@id";
        c.Parameters.AddWithValue("@id", caseId);
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> CountAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM document_chunks WHERE case_id=@id";
        c.Parameters.AddWithValue("@id", caseId);
        return (int)(await c.ExecuteScalarAsync(ct))!;
    }
}
