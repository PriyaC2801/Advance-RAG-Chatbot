using AdvancedRag.Core.Rag;
using Npgsql;
using Pgvector.Npgsql;

namespace AdvancedRag.Core.Storage;

/// <summary>
/// The single PostgreSQL + pgvector database shared by all pipelines. Each pipeline owns its own table(s)
/// (gep_chunks, gpo_chunks, imac_chunks), so documents never see each other's vectors.
/// </summary>
public static class VectorDb
{
    public const string ConnectionStringName = "VectorDb";

    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        return builder.Build();
    }

    /// <summary>
    /// Opens a connection and makes sure the <c>vector</c> extension exists. Npgsql can only map the type after the extension
    /// exists, so the types are reloaded once. Translates connection failures into <see cref="VectorStoreUnavailableException"/>.
    /// </summary>
    public static async Task<NpgsqlConnection> OpenAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using (var extension = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector", connection))
            {
                await extension.ExecuteNonQueryAsync(cancellationToken);
            }
            await connection.ReloadTypesAsync(cancellationToken);
            return connection;
        }
        catch (Exception ex) when (IsConnectivityFailure(ex))
        {
            throw Unavailable(ex);
        }
    }

    public static bool IsConnectivityFailure(Exception ex) => ex is NpgsqlException or System.Net.Sockets.SocketException;

    public static VectorStoreUnavailableException Unavailable(Exception ex) => new(
        "The vector database (PostgreSQL + pgvector) is not reachable. Start it with 'docker compose up -d' and try again.", ex);

    /// <summary>Table names are written into SQL text (identifiers cannot be parameters), so they are validated.</summary>
    public static string SafeTableName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,62}$")
            ? name
            : throw new InvalidOperationException($"'{name}' is not a valid table name: use letters, digits and underscores only.");
}
