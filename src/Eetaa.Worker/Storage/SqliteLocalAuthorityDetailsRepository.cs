using Eetaa.Worker.Data;

using Microsoft.Data.Sqlite;

namespace Eetaa.Worker.Storage;

public interface ILocalAuthorityDetailsRepository
{
    Task SaveLocalAuthorityChangesAsync(LocalAuthorityPayload payload, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalAuthorityDetailRecord>> GetChangesByAuthorityIdAsync(long authorityId, CancellationToken cancellationToken = default);
    Task<long> GetTotalChangesCountAsync(CancellationToken cancellationToken = default);
}

public sealed class SqliteLocalAuthorityDetailsRepository : ILocalAuthorityDetailsRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;

    public SqliteLocalAuthorityDetailsRepository(ISqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task SaveLocalAuthorityChangesAsync(LocalAuthorityPayload payload, CancellationToken cancellationToken = default)
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
                UPDATE local_authority_queue
                SET status = 'failed',
                    attempt_count = attempt_count + 1,
                    last_attempt_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    error_message = @error_message,
                    updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                WHERE authority_id = @authority_id;
            ";
            failCmd.Parameters.AddWithValue("@authority_id", payload.AuthorityId);
            failCmd.Parameters.AddWithValue("@error_message", payload.ErrorMessage);

            await failCmd.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // Idempotent write: delete existing details for this local authority
        using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM dkmet_details WHERE authority_id = @authority_id;";
            deleteCmd.Parameters.AddWithValue("@authority_id", payload.AuthorityId);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Insert all changes if any
        if (payload.Changes.Count > 0)
        {
            using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = transaction;
            insertCmd.CommandText = @"
                INSERT INTO dkmet_details (
                    authority_id,
                    change_order,
                    ota_type,
                    authority_name,
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
                    @authority_id,
                    @change_order,
                    @ota_type,
                    @authority_name,
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

            var aidParam = insertCmd.Parameters.Add("@authority_id", SqliteType.Integer);
            var orderParam = insertCmd.Parameters.Add("@change_order", SqliteType.Integer);
            var typeParam = insertCmd.Parameters.Add("@ota_type", SqliteType.Text);
            var nameParam = insertCmd.Parameters.Add("@authority_name", SqliteType.Text);
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
                aidParam.Value = change.AuthorityId;
                orderParam.Value = change.ChangeOrder;
                typeParam.Value = (object?)change.AuthorityType?.ToDbCode() ?? DBNull.Value;
                nameParam.Value = change.AuthorityName;
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

        // Update local_authority_queue to completed
        using (var completeCmd = connection.CreateCommand())
        {
            completeCmd.Transaction = transaction;
            completeCmd.CommandText = @"
                UPDATE local_authority_queue
                SET status = 'completed',
                    attempt_count = attempt_count + 1,
                    last_attempt_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    completed_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    error_message = NULL,
                    updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
                WHERE authority_id = @authority_id;
            ";
            completeCmd.Parameters.AddWithValue("@authority_id", payload.AuthorityId);
            await completeCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalAuthorityDetailRecord>> GetChangesByAuthorityIdAsync(
        long authorityId,
        CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT
                authority_id,
                change_order,
                ota_type,
                authority_name,
                nomos,
                change_year,
                fek_number,
                fek_issue,
                fek_date,
                fek_date_raw,
                change_description,
                raw_record
            FROM dkmet_details
            WHERE authority_id = @authority_id
            ORDER BY change_order ASC;
        ";
        cmd.Parameters.AddWithValue("@authority_id", authorityId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var results = new List<LocalAuthorityDetailRecord>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var otaTypeStr = reader.IsDBNull(2) ? null : reader.GetString(2);
            results.Add(new LocalAuthorityDetailRecord(
                AuthorityId: reader.GetInt64(0),
                ChangeOrder: reader.GetInt32(1),
                AuthorityType: AuthorityTypeExtensions.FromCode(otaTypeStr),
                AuthorityName: reader.GetString(3),
                Nomos: reader.IsDBNull(4) ? null : reader.GetString(4),
                ChangeYear: reader.IsDBNull(5) ? null : reader.GetInt32(5),
                FekNumber: reader.IsDBNull(6) ? null : reader.GetString(6),
                FekIssue: reader.IsDBNull(7) ? null : reader.GetString(7),
                FekDate: reader.IsDBNull(8) ? null : reader.GetString(8),
                FekDateRaw: reader.IsDBNull(9) ? null : reader.GetString(9),
                ChangeDescription: reader.GetString(10),
                RawRecord: reader.IsDBNull(11) ? null : reader.GetString(11)
            ));
        }

        return results;
    }

    public async Task<long> GetTotalChangesCountAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dkmet_details;";
        var res = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(res);
    }
}
