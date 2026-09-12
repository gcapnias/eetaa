using Eetaa.Worker.Discovery;

using Microsoft.Data.Sqlite;

namespace Eetaa.Worker.Data;

public interface ISqliteQueueRepository
{
    Task InitializeDatabaseAsync(CancellationToken cancellationToken = default);
    Task<int> UpsertDiscoveredSettlementsAsync(IReadOnlyCollection<DiscoveredSettlement> settlements, CancellationToken cancellationToken = default);
    Task<int> ResetInFlightProcessingAsync(CancellationToken cancellationToken = default);
    Task<long> GetCountAsync(CancellationToken cancellationToken = default);
    Task<long> GetCountByStatusAsync(string status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Eetaa.Worker.Ingestion.QueueSettlementItem>> GetPendingSettlementsAsync(
        bool includeFailed,
        bool forceAll,
        int? limit = null,
        CancellationToken cancellationToken = default);
    Task<bool> MarkProcessingAsync(long settlementId, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(long settlementId, string errorMessage, CancellationToken cancellationToken = default);
}

public sealed class SqliteQueueRepository : ISqliteQueueRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteQueueRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task InitializeDatabaseAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        // Apply performance and integrity pragmas
        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = @"
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
            ";
            await pragmaCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Create settlement_queue table and index
        using (var createCmd = connection.CreateCommand())
        {
            createCmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS settlement_queue (
                    settlement_id INTEGER PRIMARY KEY,
                    name_hint TEXT,
                    status TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'processing', 'completed', 'failed')),
                    attempt_count INTEGER NOT NULL DEFAULT 0,
                    last_attempt_at TEXT NULL,
                    completed_at TEXT NULL,
                    error_message TEXT NULL,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                );

                CREATE INDEX IF NOT EXISTS idx_settlement_queue_status ON settlement_queue (status);

                CREATE TABLE IF NOT EXISTS oikmet_details (
                    settlement_id INTEGER NOT NULL,
                    change_order INTEGER NOT NULL,
                    settlement_name TEXT NOT NULL,
                    nomos TEXT NULL,
                    change_year INTEGER NULL,
                    fek_number TEXT NULL,
                    fek_issue TEXT NULL,
                    fek_date TEXT NULL,
                    fek_date_raw TEXT NULL,
                    change_description TEXT NOT NULL,
                    raw_record TEXT NULL,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    PRIMARY KEY (settlement_id, change_order),
                    FOREIGN KEY (settlement_id) REFERENCES settlement_queue(settlement_id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_oikmet_details_settlement_id ON oikmet_details (settlement_id);
                CREATE INDEX IF NOT EXISTS idx_oikmet_details_fek_date ON oikmet_details (fek_date);

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

                CREATE TABLE IF NOT EXISTS dkmet_details (
                    authority_id INTEGER NOT NULL,
                    change_order INTEGER NOT NULL,
                    ota_type TEXT NULL,
                    authority_name TEXT NOT NULL,
                    nomos TEXT NULL,
                    change_year INTEGER NULL,
                    fek_number TEXT NULL,
                    fek_issue TEXT NULL,
                    fek_date TEXT NULL,
                    fek_date_raw TEXT NULL,
                    change_description TEXT NOT NULL,
                    raw_record TEXT NULL,
                    created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                    PRIMARY KEY (authority_id, change_order),
                    FOREIGN KEY (authority_id) REFERENCES local_authority_queue(authority_id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_dkmet_details_authority_id ON dkmet_details (authority_id);
                CREATE INDEX IF NOT EXISTS idx_dkmet_details_fek_date ON dkmet_details (fek_date);
            ";
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<int> UpsertDiscoveredSettlementsAsync(
        IReadOnlyCollection<DiscoveredSettlement> settlements,
        CancellationToken cancellationToken = default)
    {
        if (settlements.Count == 0)
        {
            return 0;
        }

        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();
        var insertedCount = 0;

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = transaction;
            cmd.CommandText = @"
                INSERT OR IGNORE INTO settlement_queue (
                    settlement_id,
                    name_hint,
                    status,
                    attempt_count,
                    created_at,
                    updated_at
                ) VALUES (
                    @settlement_id,
                    @name_hint,
                    'pending',
                    0,
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                );
            ";

            var idParam = cmd.Parameters.Add("@settlement_id", SqliteType.Integer);
            var nameParam = cmd.Parameters.Add("@name_hint", SqliteType.Text);

            foreach (var settlement in settlements)
            {
                idParam.Value = settlement.SettlementId;
                nameParam.Value = (object?)settlement.NameHint ?? DBNull.Value;

                var affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
                insertedCount += affected;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return insertedCount;
    }

    public async Task<int> ResetInFlightProcessingAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE settlement_queue
            SET status = 'pending',
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE status = 'processing';
        ";

        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> GetCountAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM settlement_queue;";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    public async Task<long> GetCountByStatusAsync(string status, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM settlement_queue WHERE status = @status;";
        cmd.Parameters.AddWithValue("@status", status);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result);
    }

    public async Task<IReadOnlyList<Eetaa.Worker.Ingestion.QueueSettlementItem>> GetPendingSettlementsAsync(
        bool includeFailed,
        bool forceAll,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
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
            SELECT settlement_id, name_hint, status, attempt_count
            FROM settlement_queue
            WHERE {whereClause}
            ORDER BY settlement_id ASC
            {limitClause};
        ";

        var list = new List<Eetaa.Worker.Ingestion.QueueSettlementItem>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var nameHint = reader.IsDBNull(1) ? null : reader.GetString(1);
            var status = reader.GetString(2);
            var attemptCount = reader.GetInt32(3);
            list.Add(new Eetaa.Worker.Ingestion.QueueSettlementItem(id, nameHint, status, attemptCount));
        }

        return list;
    }

    public async Task<bool> MarkProcessingAsync(long settlementId, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE settlement_queue
            SET status = 'processing',
                last_attempt_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE settlement_id = @settlement_id;
        ";
        cmd.Parameters.AddWithValue("@settlement_id", settlementId);

        var rows = await cmd.ExecuteNonQueryAsync(cancellationToken);
        return rows > 0;
    }

    public async Task MarkFailedAsync(long settlementId, string errorMessage, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE settlement_queue
            SET status = 'failed',
                attempt_count = attempt_count + 1,
                last_attempt_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                error_message = @error_message,
                updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
            WHERE settlement_id = @settlement_id;
        ";
        cmd.Parameters.AddWithValue("@settlement_id", settlementId);
        cmd.Parameters.AddWithValue("@error_message", errorMessage);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}