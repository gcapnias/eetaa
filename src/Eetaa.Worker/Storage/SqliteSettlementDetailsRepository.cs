using Eetaa.Worker.Data;

using Microsoft.Data.Sqlite;

namespace Eetaa.Worker.Storage;

public interface ISettlementDetailsRepository
{
    Task SaveSettlementChangesAsync(SettlementPayload payload, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SettlementDetailRecord>> GetChangesBySettlementIdAsync(long settlementId, CancellationToken cancellationToken = default);
    Task<long> GetTotalChangesCountAsync(CancellationToken cancellationToken = default);
}

public sealed class SqliteSettlementDetailsRepository : ISettlementDetailsRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteSettlementDetailsRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task SaveSettlementChangesAsync(SettlementPayload payload, CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var transaction = connection.BeginTransaction();

        if (!string.IsNullOrEmpty(payload.ErrorMessage))
        {
            // Mark queue record as failed
            using var failCmd = connection.CreateCommand();
            failCmd.Transaction = transaction;
            failCmd.CommandText = @"
                UPDATE settlement_queue
                SET status = 'failed',
                    attempt_count = attempt_count + 1,
                    last_attempt_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    error_message = @error_message,
                    updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                WHERE settlement_id = @settlement_id;
            ";
            failCmd.Parameters.AddWithValue("@settlement_id", payload.SettlementId);
            failCmd.Parameters.AddWithValue("@error_message", payload.ErrorMessage);

            await failCmd.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // Idempotent write: delete existing details for this settlement
        using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM oikmet_details WHERE settlement_id = @settlement_id;";
            deleteCmd.Parameters.AddWithValue("@settlement_id", payload.SettlementId);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Insert all changes if any
        if (payload.Changes.Count > 0)
        {
            using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = @"
                INSERT INTO oikmet_details (
                    settlement_id,
                    change_order,
                    settlement_name,
                    nomos,
                    change_year,
                    fek_number,
                    fek_issue,
                    fek_date,
                    fek_date_raw,
                    change_description,
                    raw_record,
                    created_at
                ) VALUES (
                    @settlement_id,
                    @change_order,
                    @settlement_name,
                    @nomos,
                    @change_year,
                    @fek_number,
                    @fek_issue,
                    @fek_date,
                    @fek_date_raw,
                    @change_description,
                    @raw_record,
                    strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                );
            ";

            var sidParam = insertCmd.Parameters.Add("@settlement_id", SqliteType.Integer);
            var orderParam = insertCmd.Parameters.Add("@change_order", SqliteType.Integer);
            var nameParam = insertCmd.Parameters.Add("@settlement_name", SqliteType.Text);
            var nomosParam = insertCmd.Parameters.Add("@nomos", SqliteType.Text);
            var yearParam = insertCmd.Parameters.Add("@change_year", SqliteType.Integer);
            var fekNumParam = insertCmd.Parameters.Add("@fek_number", SqliteType.Text);
            var fekIssueParam = insertCmd.Parameters.Add("@fek_issue", SqliteType.Text);
            var fekDateParam = insertCmd.Parameters.Add("@fek_date", SqliteType.Text);
            var fekDateRawParam = insertCmd.Parameters.Add("@fek_date_raw", SqliteType.Text);
            var descParam = insertCmd.Parameters.Add("@change_description", SqliteType.Text);
            var rawRecParam = insertCmd.Parameters.Add("@raw_record", SqliteType.Text);

            foreach (var change in payload.Changes)
            {
                sidParam.Value = change.SettlementId;
                orderParam.Value = change.ChangeOrder;
                nameParam.Value = change.SettlementName;
                nomosParam.Value = (object?)change.Nomos ?? DBNull.Value;
                yearParam.Value = (object?)change.ChangeYear ?? DBNull.Value;
                fekNumParam.Value = (object?)change.FekNumber ?? DBNull.Value;
                fekIssueParam.Value = (object?)change.FekIssue ?? DBNull.Value;
                fekDateParam.Value = (object?)change.FekDate ?? DBNull.Value;
                fekDateRawParam.Value = (object?)change.FekDateRaw ?? DBNull.Value;
                descParam.Value = change.ChangeDescription;
                rawRecParam.Value = (object?)change.RawRecord ?? DBNull.Value;

                await insertCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // Update settlement_queue to completed
        using (var completeCmd = connection.CreateCommand())
        {
            completeCmd.Transaction = transaction;
            completeCmd.CommandText = @"
                UPDATE settlement_queue
                SET status = 'completed',
                    attempt_count = attempt_count + 1,
                    last_attempt_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    completed_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    error_message = NULL,
                    updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                WHERE settlement_id = @settlement_id;
            ";
            completeCmd.Parameters.AddWithValue("@settlement_id", payload.SettlementId);
            await completeCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SettlementDetailRecord>> GetChangesBySettlementIdAsync(
        long settlementId,
        CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                settlement_id,
                change_order,
                settlement_name,
                nomos,
                change_year,
                fek_number,
                fek_issue,
                fek_date,
                fek_date_raw,
                change_description,
                raw_record
            FROM oikmet_details
            WHERE settlement_id = @settlement_id
            ORDER BY change_order ASC;
        ";
        cmd.Parameters.AddWithValue("@settlement_id", settlementId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var results = new List<SettlementDetailRecord>();

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SettlementDetailRecord(
                SettlementId: reader.GetInt64(0),
                ChangeOrder: reader.GetInt32(1),
                SettlementName: reader.GetString(2),
                Nomos: reader.IsDBNull(3) ? null : reader.GetString(3),
                ChangeYear: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                FekNumber: reader.IsDBNull(5) ? null : reader.GetString(5),
                FekIssue: reader.IsDBNull(6) ? null : reader.GetString(6),
                FekDate: reader.IsDBNull(7) ? null : reader.GetString(7),
                FekDateRaw: reader.IsDBNull(8) ? null : reader.GetString(8),
                ChangeDescription: reader.GetString(9),
                RawRecord: reader.IsDBNull(10) ? null : reader.GetString(10)
            ));
        }

        return results;
    }

    public async Task<long> GetTotalChangesCountAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM oikmet_details;";
        var res = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(res);
    }
}