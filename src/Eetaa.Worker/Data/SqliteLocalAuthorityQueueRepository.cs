using Eetaa.Worker.Discovery;
using Eetaa.Worker.Ingestion;

using Microsoft.Data.Sqlite;

namespace Eetaa.Worker.Data;

public interface ISqliteLocalAuthorityQueueRepository
{
    Task InitializeDatabaseAsync(CancellationToken cancellationToken = default);
    Task<int> UpsertDiscoveredAuthoritiesAsync(IReadOnlyCollection<DiscoveredLocalAuthority> authorities, CancellationToken cancellationToken = default);
    Task<int> ResetInFlightProcessingAsync(CancellationToken cancellationToken = default);
    Task<long> GetCountAsync(CancellationToken cancellationToken = default);
    Task<long> GetCountByStatusAsync(string status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<QueueLocalAuthorityItem>> GetPendingAuthoritiesAsync(
        bool includeFailed,
        bool forceAll,
        int? limit = null,
        CancellationToken cancellationToken = default);
    Task<bool> MarkProcessingAsync(long authorityId, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(long authorityId, string errorMessage, CancellationToken cancellationToken = default);
}

public sealed class SqliteLocalAuthorityQueueRepository(ISqliteConnectionFactory connectionFactory) : ISqliteLocalAuthorityQueueRepository
{
    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        // Apply performance pragmas
        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
            ";
            await pragmaCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Create local_authority_queue table and index
        using (var createCmd = connection.CreateCommand())
        {
            createCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS local_authority_queue (
                    authority_id INTEGER PRIMARY KEY,
                    name_hint TEXT,
                    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'processing', 'completed', 'failed')),
                    attempt_count INTEGER NOT NULL DEFAULT 0,
                    last_attempt_at TEXT NULL,
                    completed_at TEXT NULL,
                    error_message TEXT NULL,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                );

                CREATE INDEX IF NOT EXISTS idx_local_authority_queue_status ON local_authority_queue (status);
            ";
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<int> UpsertDiscoveredAuthoritiesAsync(
        IReadOnlyCollection<DiscoveredLocalAuthority> authorities,
        CancellationToken cancellationToken = default)
    {
        if (authorities.Count == 0)
        {
            return 0;
        }

        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();
        var insertedCount = 0;

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT OR IGNORE INTO local_authority_queue (
                    authority_id,
                    name_hint,
                    status,
                    attempt_count,
                    created_at,
                    updated_at
                ) VALUES (
                    @authority_id,
                    @name_hint,
                    'pending',
                    0,
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                );
            ";

            var idParam = cmd.Parameters.Add("@authority_id", SqliteType.Integer);
            var nameParam = cmd.Parameters.Add("@name_hint", SqliteType.Text);

            foreach (var authority in authorities)
            {
                idParam.Value = authority.AuthorityId;
                nameParam.Value = (object?)authority.NameHint ?? DBNull.Value;

                var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                insertedCount += affected;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return insertedCount;
    }

    public async Task<int> ResetInFlightProcessingAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE local_authority_queue
            SET status = 'pending',
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE status = 'processing';
        ";

        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> GetCountAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM local_authority_queue;";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    public async Task<long> GetCountByStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM local_authority_queue WHERE status = @status;";
        cmd.Parameters.AddWithValue("@status", status);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    public async Task<IReadOnlyList<QueueLocalAuthorityItem>> GetPendingAuthoritiesAsync(
        bool includeFailed,
        bool forceAll,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();

        string whereClause;
        if (forceAll)
        {
            whereClause = "1=1";
        }
        else if (includeFailed)
        {
            whereClause = "status IN ('pending', 'failed')";
        }
        else
        {
            whereClause = "status = 'pending'";
        }

        var limitClause = limit.HasValue ? $"LIMIT {limit.Value}" : string.Empty;
        cmd.CommandText = $@"
            SELECT authority_id, name_hint, status, attempt_count
            FROM local_authority_queue
            WHERE {whereClause}
            ORDER BY authority_id ASC
            {limitClause};
        ";

        var list = new List<QueueLocalAuthorityItem>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var nameHint = reader.IsDBNull(1) ? null : reader.GetString(1);
            var status = reader.GetString(2);
            var attemptCount = reader.GetInt32(3);
            list.Add(new QueueLocalAuthorityItem(id, nameHint, status, attemptCount));
        }

        return list;
    }

    public async Task<bool> MarkProcessingAsync(long authorityId, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE local_authority_queue
            SET status = 'processing',
                last_attempt_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE authority_id = @authority_id;
        ";
        cmd.Parameters.AddWithValue("@authority_id", authorityId);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task MarkFailedAsync(long authorityId, string errorMessage, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE local_authority_queue
            SET status = 'failed',
                attempt_count = attempt_count + 1,
                last_attempt_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                error_message = @error_message,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE authority_id = @authority_id;
        ";
        cmd.Parameters.AddWithValue("@authority_id", authorityId);
        cmd.Parameters.AddWithValue("@error_message", errorMessage);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
