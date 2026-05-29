// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.PostgreSql.Features.Schema;
using Npgsql;

namespace Microsoft.Health.Fhir.PostgreSql.Features.Storage
{
    /// <summary>
    /// Hosted service that applies missing schema migrations to the PostgreSQL database on startup.
    /// Migrations are embedded SQL scripts named <c>{version}.sql</c> stored under
    /// <c>Features/Schema/Migrations/</c>.
    /// </summary>
    public class PostgreSqlSchemaInitializer : IHostedService
    {
        private readonly INpgsqlConnectionFactory _connectionFactory;
        private readonly ILogger<PostgreSqlSchemaInitializer> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="PostgreSqlSchemaInitializer"/> class.
        /// </summary>
        /// <param name="connectionFactory">Factory for creating database connections.</param>
        /// <param name="logger">Logger instance.</param>
        public PostgreSqlSchemaInitializer(
            INpgsqlConnectionFactory connectionFactory,
            ILogger<PostgreSqlSchemaInitializer> logger)
        {
            _connectionFactory = EnsureArg.IsNotNull(connectionFactory, nameof(connectionFactory));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting PostgreSQL schema initialization...");

            await using var connection = await _connectionFactory.GetConnectionAsync(cancellationToken);

            // Ensure the schema version tracking table exists
            await using (var createCmd = connection.CreateCommand())
            {
                createCmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS schema_version
                    (
                        version INTEGER PRIMARY KEY,
                        status  VARCHAR(10) NOT NULL
                    )";
                await createCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var appliedVersions = await GetAppliedVersionsAsync(connection, cancellationToken);
            var assembly = Assembly.GetExecutingAssembly();

            for (var version = SchemaVersionConstants.Min; version <= SchemaVersionConstants.Max; version++)
            {
                if (appliedVersions.Contains(version))
                {
                    continue;
                }

                var sql = ReadEmbeddedMigration(assembly, version);
                if (sql == null)
                {
                    _logger.LogWarning("Migration script for version {Version} not found; skipping.", version);
                    continue;
                }

                _logger.LogInformation("Applying PostgreSQL schema migration version {Version}.", version);

                await using var tx = await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    await using var migCmd = connection.CreateCommand();
                    migCmd.Transaction = tx;
#pragma warning disable CA2100 // SQL comes from an embedded resource — not user input
                    migCmd.CommandText = sql;
#pragma warning restore CA2100
                    await migCmd.ExecuteNonQueryAsync(cancellationToken);

                    await using var versionCmd = connection.CreateCommand();
                    versionCmd.Transaction = tx;
                    versionCmd.CommandText = "INSERT INTO schema_version (version, status) VALUES (@version, 'complete') ON CONFLICT DO NOTHING";
                    versionCmd.Parameters.AddWithValue("version", version);
                    await versionCmd.ExecuteNonQueryAsync(cancellationToken);

                    await tx.CommitAsync(cancellationToken);
                    _logger.LogInformation("Schema migration version {Version} applied successfully.", version);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _logger.LogError(ex, "Failed to apply schema migration version {Version}.", version);
                    throw;
                }
            }

            _logger.LogInformation("PostgreSQL schema initialization complete.");
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static async Task<System.Collections.Generic.HashSet<int>> GetAppliedVersionsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
        {
            var versions = new System.Collections.Generic.HashSet<int>();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT version FROM schema_version WHERE status = 'complete'";
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                versions.Add(reader.GetInt32(0));
            }

            return versions;
        }

        private static string ReadEmbeddedMigration(Assembly assembly, int version)
        {
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($"Migrations.{version}.sql", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
