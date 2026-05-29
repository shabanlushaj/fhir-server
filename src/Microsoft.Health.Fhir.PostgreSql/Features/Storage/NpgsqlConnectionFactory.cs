// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.PostgreSql.Registration;
using Npgsql;

namespace Microsoft.Health.Fhir.PostgreSql.Features.Storage
{
    /// <summary>
    /// Default implementation of <see cref="INpgsqlConnectionFactory"/> that creates connections
    /// from the configured connection string.
    /// </summary>
    public class NpgsqlConnectionFactory : INpgsqlConnectionFactory
    {
        private readonly NpgsqlDataSource _dataSource;

        /// <summary>
        /// Initializes a new instance of the <see cref="NpgsqlConnectionFactory"/> class.
        /// </summary>
        /// <param name="configuration">PostgreSQL data store configuration.</param>
        public NpgsqlConnectionFactory(IOptions<PostgreSqlDataStoreConfiguration> configuration)
        {
            EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            EnsureArg.IsNotNullOrWhiteSpace(configuration.Value.ConnectionString, nameof(configuration.Value.ConnectionString));

            var builder = new NpgsqlDataSourceBuilder(configuration.Value.ConnectionString);
            _dataSource = builder.Build();
        }

        /// <inheritdoc />
        public async Task<NpgsqlConnection> GetConnectionAsync(CancellationToken cancellationToken)
        {
            return await _dataSource.OpenConnectionAsync(cancellationToken);
        }
    }
}
