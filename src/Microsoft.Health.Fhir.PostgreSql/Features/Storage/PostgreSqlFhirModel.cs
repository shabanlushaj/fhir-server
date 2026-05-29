// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Microsoft.Health.Fhir.PostgreSql.Features.Storage
{
    /// <summary>
    /// Maintains in-memory maps between string identifiers (resource type names, search parameter URIs,
    /// system URIs, quantity codes) and their corresponding integer IDs stored in the PostgreSQL database.
    /// Keeps ID lookups out of the hot query path.
    /// </summary>
    public class PostgreSqlFhirModel
    {
        private readonly INpgsqlConnectionFactory _connectionFactory;
        private readonly ILogger<PostgreSqlFhirModel> _logger;

        private Dictionary<string, short> _resourceTypeToId = new(StringComparer.Ordinal);
        private Dictionary<short, string> _resourceTypeIdToName = new();
        private Dictionary<string, short> _searchParamUriToId = new(StringComparer.Ordinal);
        private ConcurrentDictionary<string, int> _systemToId = new(StringComparer.Ordinal);
        private ConcurrentDictionary<string, int> _quantityCodeToId = new(StringComparer.Ordinal);

        private int _initialized;

        /// <summary>
        /// Initializes a new instance of the <see cref="PostgreSqlFhirModel"/> class.
        /// </summary>
        /// <param name="connectionFactory">Factory used to obtain database connections.</param>
        /// <param name="logger">Logger instance.</param>
        public PostgreSqlFhirModel(INpgsqlConnectionFactory connectionFactory, ILogger<PostgreSqlFhirModel> logger)
        {
            _connectionFactory = EnsureArg.IsNotNull(connectionFactory, nameof(connectionFactory));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <summary>
        /// Gets the lowest known resource type ID.
        /// </summary>
        public short LowestResourceTypeId { get; private set; }

        /// <summary>
        /// Gets the highest known resource type ID.
        /// </summary>
        public short HighestResourceTypeId { get; private set; }

        /// <summary>
        /// Ensures the in-memory model is populated from the database. Safe to call multiple times.
        /// </summary>
        public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
            {
                await LoadFromDatabaseAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Gets the numeric ID for a resource type name. Inserts the type if unknown.
        /// </summary>
        public async Task<short> GetOrCreateResourceTypeIdAsync(string resourceTypeName, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(resourceTypeName, nameof(resourceTypeName));

            if (_resourceTypeToId.TryGetValue(resourceTypeName, out var id))
            {
                return id;
            }

            return await UpsertResourceTypeAsync(resourceTypeName, cancellationToken);
        }

        /// <summary>
        /// Gets the resource type name for a given ID.
        /// </summary>
        public string GetResourceTypeName(short resourceTypeId)
        {
            return _resourceTypeIdToName.TryGetValue(resourceTypeId, out var name) ? name : null;
        }

        /// <summary>
        /// Tries to get the numeric ID for a resource type name.
        /// </summary>
        public bool TryGetResourceTypeId(string resourceTypeName, out short id)
        {
            id = 0;
            if (string.IsNullOrEmpty(resourceTypeName))
            {
                return false;
            }

            return _resourceTypeToId.TryGetValue(resourceTypeName, out id);
        }

        /// <summary>
        /// Gets the numeric ID for a search parameter URI.
        /// </summary>
        public bool TryGetSearchParamId(string uri, out short id)
        {
            id = 0;
            if (string.IsNullOrEmpty(uri))
            {
                return false;
            }

            return _searchParamUriToId.TryGetValue(uri, out id);
        }

        /// <summary>
        /// Gets or creates a system ID for the given system URI value.
        /// </summary>
        public async Task<int> GetOrCreateSystemIdAsync(string system, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(system))
            {
                return 0;
            }

            if (_systemToId.TryGetValue(system, out var id))
            {
                return id;
            }

            return await UpsertSystemAsync(system, cancellationToken);
        }

        /// <summary>
        /// Gets or creates a quantity code ID for the given code value.
        /// </summary>
        public async Task<int> GetOrCreateQuantityCodeIdAsync(string code, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(code))
            {
                return 0;
            }

            if (_quantityCodeToId.TryGetValue(code, out var id))
            {
                return id;
            }

            return await UpsertQuantityCodeAsync(code, cancellationToken);
        }

        private async Task LoadFromDatabaseAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Loading PostgreSQL FHIR model from database...");

            await using var connection = await _connectionFactory.GetConnectionAsync(cancellationToken);

            // Load resource types
            var resourceTypes = new Dictionary<string, short>(StringComparer.Ordinal);
            var resourceTypeIdToName = new Dictionary<short, string>();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT resource_type_id, name FROM resource_type ORDER BY resource_type_id";
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetInt16(0);
                    var name = reader.GetString(1);
                    resourceTypes[name] = id;
                    resourceTypeIdToName[id] = name;
                }
            }

            _resourceTypeToId = resourceTypes;
            _resourceTypeIdToName = resourceTypeIdToName;

            if (resourceTypeIdToName.Count > 0)
            {
                LowestResourceTypeId = resourceTypeIdToName.Keys.Min();
                HighestResourceTypeId = resourceTypeIdToName.Keys.Max();
            }

            // Load search params
            var searchParams = new Dictionary<string, short>(StringComparer.Ordinal);
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT search_param_id, uri FROM search_param ORDER BY search_param_id";
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    searchParams[reader.GetString(1)] = reader.GetInt16(0);
                }
            }

            _searchParamUriToId = searchParams;

            // Load systems
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT system_id, value FROM system";
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    _systemToId[reader.GetString(1)] = reader.GetInt32(0);
                }
            }

            // Load quantity codes
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT quantity_code_id, value FROM quantity_code";
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    _quantityCodeToId[reader.GetString(1)] = reader.GetInt32(0);
                }
            }

            _logger.LogInformation(
                "PostgreSQL FHIR model loaded. ResourceTypes={ResourceTypeCount}, SearchParams={SearchParamCount}",
                _resourceTypeToId.Count,
                _searchParamUriToId.Count);
        }

        private async Task<short> UpsertResourceTypeAsync(string name, CancellationToken cancellationToken)
        {
            await using var connection = await _connectionFactory.GetConnectionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO resource_type (name) VALUES (@name)
                ON CONFLICT (name) DO UPDATE SET name = EXCLUDED.name
                RETURNING resource_type_id";
            cmd.Parameters.AddWithValue("name", name);
            var id = (short)(int)await cmd.ExecuteScalarAsync(cancellationToken);
            _resourceTypeToId[name] = id;
            _resourceTypeIdToName[id] = name;
            HighestResourceTypeId = Math.Max(HighestResourceTypeId, id);
            if (LowestResourceTypeId == 0)
            {
                LowestResourceTypeId = id;
            }

            return id;
        }

        private async Task<int> UpsertSystemAsync(string system, CancellationToken cancellationToken)
        {
            await using var connection = await _connectionFactory.GetConnectionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO system (value) VALUES (@value)
                ON CONFLICT (value) DO UPDATE SET value = EXCLUDED.value
                RETURNING system_id";
            cmd.Parameters.AddWithValue("value", system);
            var id = (int)await cmd.ExecuteScalarAsync(cancellationToken);
            _systemToId[system] = id;
            return id;
        }

        private async Task<int> UpsertQuantityCodeAsync(string code, CancellationToken cancellationToken)
        {
            await using var connection = await _connectionFactory.GetConnectionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO quantity_code (value) VALUES (@value)
                ON CONFLICT (value) DO UPDATE SET value = EXCLUDED.value
                RETURNING quantity_code_id";
            cmd.Parameters.AddWithValue("value", code);
            var id = (int)await cmd.ExecuteScalarAsync(cancellationToken);
            _quantityCodeToId[code] = id;
            return id;
        }
    }
}
