using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.SponsorBlock.State;

/// <summary>
/// SQLite-backed implementation of <see cref="ISponsorBlockStateStore"/>.
/// Owns its connection; intended to be registered as a singleton.
/// </summary>
public sealed class SqliteSponsorBlockStateStore : ISponsorBlockStateStore, IDisposable
{
	private const int SchemaVersion = 2;

	private readonly SqliteConnection _connection;
	private readonly SemaphoreSlim _writeLock = new(1, 1);

	/// <summary>
	/// Initializes the store using a caller-supplied connection. The connection is opened if not already open.
	/// </summary>
	/// <param name="connection">The SQLite connection to own.</param>
	public SqliteSponsorBlockStateStore(SqliteConnection connection)
	{
		_connection = connection;
		if (_connection.State != ConnectionState.Open)
		{
			_connection.Open();
		}

		EnsureSchema();
	}

	private void EnsureSchema()
	{
		using var versionCmd = _connection.CreateCommand();
		versionCmd.CommandText = "PRAGMA user_version";
		var currentVersion = (long)(versionCmd.ExecuteScalar() ?? 0);

		if (currentVersion != SchemaVersion)
		{
			using var dropCmd = _connection.CreateCommand();
			dropCmd.CommandText = "DROP TABLE IF EXISTS item_state";
			dropCmd.ExecuteNonQuery();

			using var createCmd = _connection.CreateCommand();
			createCmd.CommandText = @"
				CREATE TABLE item_state (
					item_id               BLOB PRIMARY KEY,
					video_id              TEXT NOT NULL,
					state                 INTEGER NOT NULL,
					first_seen_at         INTEGER NOT NULL,
					last_fetch_at         INTEGER NOT NULL,
					segment_count         INTEGER NOT NULL DEFAULT 0,
					consecutive_unchanged INTEGER NOT NULL DEFAULT 0,
					last_segment_hash     TEXT NOT NULL DEFAULT ''
				);
				CREATE INDEX IF NOT EXISTS idx_state ON item_state(state);
				CREATE INDEX IF NOT EXISTS idx_first_seen ON item_state(first_seen_at);
				PRAGMA user_version = " + SchemaVersion + ";";
			createCmd.ExecuteNonQuery();
		}
	}

	/// <inheritdoc />
	public async ValueTask<ItemStateRow?> GetAsync(Guid itemId, CancellationToken cancellationToken)
	{
		await using var cmd = _connection.CreateCommand();
		cmd.CommandText = "SELECT video_id, state, first_seen_at, last_fetch_at, segment_count, consecutive_unchanged, last_segment_hash FROM item_state WHERE item_id = $id";
		cmd.Parameters.AddWithValue("$id", itemId.ToByteArray());

		await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return new ItemStateRow(
			ItemId: itemId,
			VideoId: reader.GetString(0),
			State: (ItemState)reader.GetInt32(1),
			FirstSeenAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
			LastFetchAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
			SegmentCount: reader.GetInt32(4),
			ConsecutiveUnchanged: reader.GetInt32(5),
			LastSegmentHash: reader.GetString(6));
	}

	/// <inheritdoc />
	public async ValueTask UpsertAsync(ItemStateRow row, CancellationToken cancellationToken)
	{
		await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await using var cmd = _connection.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO item_state (item_id, video_id, state, first_seen_at, last_fetch_at, segment_count, consecutive_unchanged, last_segment_hash)
				VALUES ($id, $vid, $st, $fs, $lf, $sc, $cu, $lh)
				ON CONFLICT(item_id) DO UPDATE SET
					video_id              = excluded.video_id,
					state                 = excluded.state,
					first_seen_at         = excluded.first_seen_at,
					last_fetch_at         = excluded.last_fetch_at,
					segment_count         = excluded.segment_count,
					consecutive_unchanged = excluded.consecutive_unchanged,
					last_segment_hash     = excluded.last_segment_hash;";
			cmd.Parameters.AddWithValue("$id", row.ItemId.ToByteArray());
			cmd.Parameters.AddWithValue("$vid", row.VideoId);
			cmd.Parameters.AddWithValue("$st", (int)row.State);
			cmd.Parameters.AddWithValue("$fs", row.FirstSeenAt.ToUnixTimeSeconds());
			cmd.Parameters.AddWithValue("$lf", row.LastFetchAt.ToUnixTimeSeconds());
			cmd.Parameters.AddWithValue("$sc", row.SegmentCount);
			cmd.Parameters.AddWithValue("$cu", row.ConsecutiveUnchanged);
			cmd.Parameters.AddWithValue("$lh", row.LastSegmentHash);
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <inheritdoc />
	public async ValueTask DeleteAsync(Guid itemId, CancellationToken cancellationToken)
	{
		await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await using var cmd = _connection.CreateCommand();
			cmd.CommandText = "DELETE FROM item_state WHERE item_id = $id";
			cmd.Parameters.AddWithValue("$id", itemId.ToByteArray());
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <inheritdoc />
	public async IAsyncEnumerable<ItemStateRow> GetActiveAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var cmd = _connection.CreateCommand();
		cmd.CommandText = @"
			SELECT item_id, video_id, state, first_seen_at, last_fetch_at, segment_count, consecutive_unchanged, last_segment_hash
			FROM item_state
			WHERE state IN (0, 1, 2)
			ORDER BY first_seen_at ASC";

		await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var idBytes = (byte[])reader.GetValue(0);
			yield return new ItemStateRow(
				ItemId: new Guid(idBytes),
				VideoId: reader.GetString(1),
				State: (ItemState)reader.GetInt32(2),
				FirstSeenAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
				LastFetchAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
				SegmentCount: reader.GetInt32(5),
				ConsecutiveUnchanged: reader.GetInt32(6),
				LastSegmentHash: reader.GetString(7));
		}
	}

	/// <inheritdoc />
	public async IAsyncEnumerable<Guid> GetAllItemIdsAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var cmd = _connection.CreateCommand();
		cmd.CommandText = "SELECT item_id FROM item_state";

		await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var idBytes = (byte[])reader.GetValue(0);
			yield return new Guid(idBytes);
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		_writeLock.Dispose();
		_connection.Dispose();
	}
}
