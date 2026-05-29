// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Models;
using Npgsql;
using NpgsqlTypes;

namespace Microsoft.Health.Fhir.PostgreSql.Features.Storage
{
    /// <summary>
    /// PostgreSQL-backed implementation of <see cref="IFhirDataStore"/>.
    /// </summary>
    public class PostgreSqlFhirDataStore : IFhirDataStore, IProvideCapability
    {
        private readonly INpgsqlConnectionFactory _connectionFactory;
        private readonly PostgreSqlFhirModel _model;
        private readonly ILogger<PostgreSqlFhirDataStore> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="PostgreSqlFhirDataStore"/> class.
        /// </summary>
        /// <param name="connectionFactory">Factory for creating database connections.</param>
        /// <param name="model">In-memory model providing ID mappings.</param>
        /// <param name="logger">Logger instance.</param>
        public PostgreSqlFhirDataStore(
            INpgsqlConnectionFactory connectionFactory,
            PostgreSqlFhirModel model,
            ILogger<PostgreSqlFhirDataStore> logger)
        {
            _connectionFactory = EnsureArg.IsNotNull(connectionFactory, nameof(connectionFactory));
            _model = EnsureArg.IsNotNull(model, nameof(model));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <inheritdoc />
        public async Task<UpsertOutcome> UpsertAsync(ResourceWrapperOperation resource, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));

            await _model.EnsureInitializedAsync(cancellationToken);

            var wrapper = resource.Wrapper;
            var resourceTypeId = await _model.GetOrCreateResourceTypeIdAsync(wrapper.ResourceTypeName, cancellationToken);

            await using var connection = await _connectionFactory.GetConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var existingVersion = await GetCurrentVersionAsync(connection, resourceTypeId, wrapper.ResourceId, cancellationToken);

                var newVersion = existingVersion.HasValue ? existingVersion.Value + 1 : 1;
                if (resource.KeepVersion && !string.IsNullOrEmpty(wrapper.Version))
                {
                    newVersion = int.Parse(wrapper.Version);
                }

                var lastModified = Clock.UtcNow;
                var surrogateId = lastModified.ToSurrogateId();

                if (existingVersion.HasValue)
                {
                    await using var histCmd = connection.CreateCommand();
                    histCmd.Transaction = transaction;
                    histCmd.CommandText = @"
                        UPDATE resource
                        SET is_history = true
                        WHERE resource_type_id = @typeId
                          AND resource_id       = @resourceId
                          AND is_history        = false";
                    histCmd.Parameters.AddWithValue("typeId", resourceTypeId);
                    histCmd.Parameters.AddWithValue("resourceId", wrapper.ResourceId);
                    await histCmd.ExecuteNonQueryAsync(cancellationToken);

                    await MarkSearchIndicesHistoryAsync(connection, transaction, wrapper.ResourceSurrogateId, cancellationToken);
                }

                var rawBytes = CompressRawResource(wrapper.RawResource.Data);

                await using var insertCmd = connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = @"
                    INSERT INTO resource
                        (resource_type_id, resource_id, version, is_history, resource_surrogate_id,
                         is_deleted, request_method, raw_resource, is_raw_resource_meta_set, search_param_hash)
                    VALUES
                        (@typeId, @resourceId, @version, false, @surrogateId,
                         @isDeleted, @requestMethod, @rawResource, @isMetaSet, @searchParamHash)";
                insertCmd.Parameters.AddWithValue("typeId", resourceTypeId);
                insertCmd.Parameters.AddWithValue("resourceId", wrapper.ResourceId);
                insertCmd.Parameters.AddWithValue("version", newVersion);
                insertCmd.Parameters.AddWithValue("surrogateId", surrogateId);
                insertCmd.Parameters.AddWithValue("isDeleted", wrapper.IsDeleted);
                insertCmd.Parameters.AddWithValue("requestMethod", (object)wrapper.Request?.Method ?? DBNull.Value);
                insertCmd.Parameters.Add(new NpgsqlParameter("rawResource", NpgsqlDbType.Bytea) { Value = rawBytes });
                insertCmd.Parameters.AddWithValue("isMetaSet", wrapper.RawResource.IsMetaSet);
                insertCmd.Parameters.AddWithValue("searchParamHash", (object)wrapper.SearchParameterHash ?? DBNull.Value);
                await insertCmd.ExecuteNonQueryAsync(cancellationToken);

                await InsertSearchIndicesAsync(connection, transaction, resourceTypeId, surrogateId, wrapper, cancellationToken);

                await transaction.CommitAsync(cancellationToken);

                var updatedWrapper = new ResourceWrapper(
                    wrapper.ResourceId,
                    newVersion.ToString(),
                    wrapper.ResourceTypeName,
                    wrapper.RawResource,
                    wrapper.Request,
                    lastModified,
                    wrapper.IsDeleted,
                    wrapper.SearchIndices,
                    wrapper.CompartmentIndices,
                    wrapper.LastModifiedClaims,
                    wrapper.SearchParameterHash,
                    surrogateId);

                _logger.LogInformation(
                    "Upserted resource {ResourceType}/{ResourceId} version={Version}.",
                    wrapper.ResourceTypeName,
                    wrapper.ResourceId,
                    newVersion);

                return new UpsertOutcome(updatedWrapper, existingVersion.HasValue ? SaveOutcomeType.Updated : SaveOutcomeType.Created);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<ResourceWrapper> GetAsync(ResourceKey key, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(key, nameof(key));

            await _model.EnsureInitializedAsync(cancellationToken);

            if (!_model.TryGetResourceTypeId(key.ResourceType, out var resourceTypeId))
            {
                return null;
            }

            await using var connection = await _connectionFactory.GetConnectionAsync(cancellationToken);
            await using var cmd = connection.CreateCommand();

            if (string.IsNullOrEmpty(key.VersionId))
            {
                cmd.CommandText = @"
                    SELECT resource_surrogate_id, version, is_history, is_deleted, raw_resource,
                           is_raw_resource_meta_set, search_param_hash, request_method
                    FROM resource
                    WHERE resource_type_id = @typeId
                      AND resource_id      = @resourceId
                      AND is_history       = false
                    LIMIT 1";
            }
            else
            {
                cmd.CommandText = @"
                    SELECT resource_surrogate_id, version, is_history, is_deleted, raw_resource,
                           is_raw_resource_meta_set, search_param_hash, request_method
                    FROM resource
                    WHERE resource_type_id = @typeId
                      AND resource_id      = @resourceId
                      AND version          = @version
                    LIMIT 1";
                cmd.Parameters.AddWithValue("version", int.Parse(key.VersionId));
            }

            cmd.Parameters.AddWithValue("typeId", resourceTypeId);
            cmd.Parameters.AddWithValue("resourceId", key.Id);

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return ReadResourceWrapper(reader, key.ResourceType, key.Id);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<ResourceWrapper>> GetAsync(IReadOnlyList<ResourceKey> keys, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(keys, nameof(keys));

            if (keys.Count == 0)
            {
                return Array.Empty<ResourceWrapper>();
            }

            await _model.EnsureInitializedAsync(cancellationToken);

            var results = new List<ResourceWrapper>();
            await using var connection = await _connectionFactory.GetConnectionAsync(cancellationToken);

            foreach (var key in keys)
            {
                if (!_model.TryGetResourceTypeId(key.ResourceType, out var resourceTypeId))
                {
                    continue;
                }

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT resource_surrogate_id, version, is_history, is_deleted, raw_resource,
                           is_raw_resource_meta_set, search_param_hash, request_method
                    FROM resource
                    WHERE resource_type_id = @typeId
                      AND resource_id      = @resourceId
                      AND is_history       = false
                    LIMIT 1";
                cmd.Parameters.AddWithValue("typeId", resourceTypeId);
                cmd.Parameters.AddWithValue("resourceId", key.Id);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    results.Add(ReadResourceWrapper(reader, key.ResourceType, key.Id));
                }
            }

            return results;
        }

        /// <inheritdoc />
        public async Task HardDeleteAsync(ResourceKey key, bool keepCurrentVersion, bool allowPartialSuccess, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(key, nameof(key));

            await _model.EnsureInitializedAsync(cancellationToken);

            if (!_model.TryGetResourceTypeId(key.ResourceType, out var resourceTypeId))
            {
                return;
            }

            await using var connection = await _connectionFactory.GetConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;

                if (keepCurrentVersion)
                {
                    cmd.CommandText = @"
                        DELETE FROM resource
                        WHERE resource_type_id = @typeId
                          AND resource_id      = @resourceId
                          AND is_history       = true";
                }
                else
                {
                    cmd.CommandText = @"
                        DELETE FROM resource
                        WHERE resource_type_id = @typeId
                          AND resource_id      = @resourceId";
                }

                cmd.Parameters.AddWithValue("typeId", resourceTypeId);
                cmd.Parameters.AddWithValue("resourceId", key.Id);
                await cmd.ExecuteNonQueryAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<MergeOutcome> MergeAsync(IReadOnlyList<ResourceWrapperOperation> resources, CancellationToken cancellationToken)
        {
            return await MergeAsync(resources, new MergeOptions(), cancellationToken);
        }

        /// <inheritdoc />
        public async Task<MergeOutcome> MergeAsync(
            IReadOnlyList<ResourceWrapperOperation> resources,
            MergeOptions mergeOptions,
            CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resources, nameof(resources));

            if (resources.Count == 0)
            {
                return MergeOutcome.Empty;
            }

            var results = new Dictionary<DataStoreOperationIdentifier, DataStoreOperationOutcome>();
            foreach (var resource in resources)
            {
                var outcome = await UpsertAsync(resource, cancellationToken);
                results[resource.GetIdentifier()] = new DataStoreOperationOutcome(outcome);
            }

            return new MergeOutcome(MergeOutcomeFinalState.Completed, results);
        }

        /// <inheritdoc />
        public async Task BulkUpdateSearchParameterIndicesAsync(IReadOnlyCollection<ResourceWrapper> resources, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resources, nameof(resources));

            await _model.EnsureInitializedAsync(cancellationToken);

            await using var connection = await _connectionFactory.GetConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                foreach (var wrapper in resources)
                {
                    if (!_model.TryGetResourceTypeId(wrapper.ResourceTypeName, out var resourceTypeId))
                    {
                        continue;
                    }

                    await DeleteSearchIndicesAsync(connection, transaction, resourceTypeId, wrapper.ResourceSurrogateId, cancellationToken);
                    await InsertSearchIndicesAsync(connection, transaction, resourceTypeId, wrapper.ResourceSurrogateId, wrapper, cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<ResourceWrapper> UpdateSearchParameterIndicesAsync(ResourceWrapper resourceWrapper, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resourceWrapper, nameof(resourceWrapper));

            await BulkUpdateSearchParameterIndicesAsync(new[] { resourceWrapper }, cancellationToken);
            return resourceWrapper;
        }

        /// <inheritdoc />
        public Task<int?> GetProvisionedDataStoreCapacityAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<int?>(null);
        }

        /// <inheritdoc />
        public Task TryLogEvent(string process, string status, string text, DateTime? startDate, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Event: Process={Process} Status={Status} Text={Text}", process, status, text);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task BuildAsync(ICapabilityStatementBuilder builder, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(builder, nameof(builder));

            await builder
                .PopulateDefaultResourceInteractions()
                .SyncSearchParameters()
                .AddGlobalSearchParameters()
                .SyncProfilesAsync(cancellationToken);
        }

        private static async Task<int?> GetCurrentVersionAsync(
            NpgsqlConnection connection,
            short resourceTypeId,
            string resourceId,
            CancellationToken cancellationToken)
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT version FROM resource
                WHERE resource_type_id = @typeId
                  AND resource_id      = @resourceId
                  AND is_history       = false
                LIMIT 1";
            cmd.Parameters.AddWithValue("typeId", resourceTypeId);
            cmd.Parameters.AddWithValue("resourceId", resourceId);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            return result is DBNull || result is null ? (int?)null : Convert.ToInt32(result);
        }

        private static async Task MarkSearchIndicesHistoryAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long surrogateId,
            CancellationToken cancellationToken)
        {
            var tables = new[] { "token_search_param", "string_search_param" };
            foreach (var table in tables)
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
#pragma warning disable CA2100 // Table name is a compile-time constant
                cmd.CommandText = $"UPDATE {table} SET is_history = true WHERE resource_surrogate_id = @surrogateId";
#pragma warning restore CA2100
                cmd.Parameters.AddWithValue("surrogateId", surrogateId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        private static async Task DeleteSearchIndicesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            short resourceTypeId,
            long surrogateId,
            CancellationToken cancellationToken)
        {
            var tables = new[]
            {
                "token_search_param",
                "string_search_param",
                "uri_search_param",
                "number_search_param",
                "quantity_search_param",
                "date_time_search_param",
                "reference_search_param",
            };

            foreach (var table in tables)
            {
                await using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
#pragma warning disable CA2100 // Table name is a compile-time constant
                cmd.CommandText = $"DELETE FROM {table} WHERE resource_type_id = @typeId AND resource_surrogate_id = @surrogateId";
#pragma warning restore CA2100
                cmd.Parameters.AddWithValue("typeId", resourceTypeId);
                cmd.Parameters.AddWithValue("surrogateId", surrogateId);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        private async Task InsertSearchIndicesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            short resourceTypeId,
            long surrogateId,
            ResourceWrapper wrapper,
            CancellationToken cancellationToken)
        {
            if (wrapper.SearchIndices == null)
            {
                return;
            }

            foreach (var entry in wrapper.SearchIndices)
            {
                if (!_model.TryGetSearchParamId(entry.SearchParameter.Url?.ToString(), out var searchParamId))
                {
                    continue;
                }

                switch (entry.Value)
                {
                    case TokenSearchValue token:
                        await InsertTokenAsync(connection, transaction, resourceTypeId, surrogateId, searchParamId, token, cancellationToken);
                        break;
                    case StringSearchValue str:
                        await InsertStringAsync(connection, transaction, resourceTypeId, surrogateId, searchParamId, str, cancellationToken);
                        break;
                    case UriSearchValue uri:
                        await InsertUriAsync(connection, transaction, resourceTypeId, surrogateId, searchParamId, uri, cancellationToken);
                        break;
                    case DateTimeSearchValue dt:
                        await InsertDateTimeAsync(connection, transaction, resourceTypeId, surrogateId, searchParamId, dt, cancellationToken);
                        break;
                    case NumberSearchValue num:
                        await InsertNumberAsync(connection, transaction, resourceTypeId, surrogateId, searchParamId, num, cancellationToken);
                        break;
                    case QuantitySearchValue qty:
                        await InsertQuantityAsync(connection, transaction, resourceTypeId, surrogateId, searchParamId, qty, cancellationToken);
                        break;
                    case ReferenceSearchValue reference:
                        await InsertReferenceAsync(connection, transaction, resourceTypeId, surrogateId, searchParamId, reference, cancellationToken);
                        break;
                }
            }
        }

        private async Task InsertTokenAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            short typeId,
            long surrogateId,
            short paramId,
            TokenSearchValue token,
            CancellationToken ct)
        {
            var systemId = await _model.GetOrCreateSystemIdAsync(token.System, ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO token_search_param (resource_type_id, resource_surrogate_id, search_param_id, system_id, code)
                VALUES (@typeId, @surrogateId, @paramId, @systemId, @code)";
            cmd.Parameters.AddWithValue("typeId", typeId);
            cmd.Parameters.AddWithValue("surrogateId", surrogateId);
            cmd.Parameters.AddWithValue("paramId", paramId);
            cmd.Parameters.AddWithValue("systemId", systemId == 0 ? (object)DBNull.Value : systemId);
            cmd.Parameters.AddWithValue("code", token.Code ?? string.Empty);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task InsertStringAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            short typeId,
            long surrogateId,
            short paramId,
            StringSearchValue str,
            CancellationToken ct)
        {
            var text = str.String?.Length > 256 ? str.String[..256] : str.String;
            var overflow = str.String?.Length > 256 ? str.String : null;
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO string_search_param (resource_type_id, resource_surrogate_id, search_param_id, text, text_overflow)
                VALUES (@typeId, @surrogateId, @paramId, @text, @overflow)";
            cmd.Parameters.AddWithValue("typeId", typeId);
            cmd.Parameters.AddWithValue("surrogateId", surrogateId);
            cmd.Parameters.AddWithValue("paramId", paramId);
            cmd.Parameters.AddWithValue("text", text ?? string.Empty);
            cmd.Parameters.AddWithValue("overflow", (object)overflow ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task InsertUriAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            short typeId,
            long surrogateId,
            short paramId,
            UriSearchValue uri,
            CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO uri_search_param (resource_type_id, resource_surrogate_id, search_param_id, uri)
                VALUES (@typeId, @surrogateId, @paramId, @uri)";
            cmd.Parameters.AddWithValue("typeId", typeId);
            cmd.Parameters.AddWithValue("surrogateId", surrogateId);
            cmd.Parameters.AddWithValue("paramId", paramId);
            cmd.Parameters.AddWithValue("uri", uri.Uri ?? string.Empty);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task InsertDateTimeAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            short typeId,
            long surrogateId,
            short paramId,
            DateTimeSearchValue dt,
            CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO date_time_search_param (resource_type_id, resource_surrogate_id, search_param_id, start_date_time, end_date_time)
                VALUES (@typeId, @surrogateId, @paramId, @start, @end)";
            cmd.Parameters.AddWithValue("typeId", typeId);
            cmd.Parameters.AddWithValue("surrogateId", surrogateId);
            cmd.Parameters.AddWithValue("paramId", paramId);
            cmd.Parameters.AddWithValue("start", dt.Start.UtcDateTime);
            cmd.Parameters.AddWithValue("end", dt.End.UtcDateTime);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task InsertNumberAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            short typeId,
            long surrogateId,
            short paramId,
            NumberSearchValue num,
            CancellationToken ct)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO number_search_param (resource_type_id, resource_surrogate_id, search_param_id, single_value, low_value, high_value)
                VALUES (@typeId, @surrogateId, @paramId, @single, @low, @high)";
            cmd.Parameters.AddWithValue("typeId", typeId);
            cmd.Parameters.AddWithValue("surrogateId", surrogateId);
            cmd.Parameters.AddWithValue("paramId", paramId);
            cmd.Parameters.AddWithValue("single", (object)num.Low ?? DBNull.Value);
            cmd.Parameters.AddWithValue("low", num.Low ?? 0m);
            cmd.Parameters.AddWithValue("high", num.High ?? 0m);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task InsertQuantityAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            short typeId,
            long surrogateId,
            short paramId,
            QuantitySearchValue qty,
            CancellationToken ct)
        {
            var codeId = await _model.GetOrCreateQuantityCodeIdAsync(qty.Code, ct);
            var sysId = await _model.GetOrCreateSystemIdAsync(qty.System, ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO quantity_search_param (resource_type_id, resource_surrogate_id, search_param_id, system_id, quantity_code_id, single_value, low_value, high_value)
                VALUES (@typeId, @surrogateId, @paramId, @sysId, @codeId, @single, @low, @high)";
            cmd.Parameters.AddWithValue("typeId", typeId);
            cmd.Parameters.AddWithValue("surrogateId", surrogateId);
            cmd.Parameters.AddWithValue("paramId", paramId);
            cmd.Parameters.AddWithValue("sysId", sysId == 0 ? (object)DBNull.Value : sysId);
            cmd.Parameters.AddWithValue("codeId", codeId == 0 ? (object)DBNull.Value : codeId);
            cmd.Parameters.AddWithValue("single", (object)qty.Low ?? DBNull.Value);
            cmd.Parameters.AddWithValue("low", qty.Low ?? 0m);
            cmd.Parameters.AddWithValue("high", qty.High ?? 0m);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private async Task InsertReferenceAsync(
            NpgsqlConnection conn,
            NpgsqlTransaction tx,
            short typeId,
            long surrogateId,
            short paramId,
            ReferenceSearchValue reference,
            CancellationToken ct)
        {
            short? refTypeId = null;
            if (!string.IsNullOrEmpty(reference.ResourceType) && _model.TryGetResourceTypeId(reference.ResourceType, out var rid))
            {
                refTypeId = rid;
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO reference_search_param (resource_type_id, resource_surrogate_id, search_param_id, base_uri, reference_resource_type_id, reference_resource_id)
                VALUES (@typeId, @surrogateId, @paramId, @baseUri, @refTypeId, @refId)";
            cmd.Parameters.AddWithValue("typeId", typeId);
            cmd.Parameters.AddWithValue("surrogateId", surrogateId);
            cmd.Parameters.AddWithValue("paramId", paramId);
            cmd.Parameters.AddWithValue("baseUri", (object)reference.BaseUri?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("refTypeId", (object)refTypeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("refId", reference.ResourceId ?? string.Empty);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static ResourceWrapper ReadResourceWrapper(NpgsqlDataReader reader, string resourceType, string resourceId)
        {
            var surrogateId = reader.GetInt64(0);
            var version = reader.GetInt32(1).ToString();
            var isHistory = reader.GetBoolean(2);
            var isDeleted = reader.GetBoolean(3);
            var rawBytes = (byte[])reader[4];
            var isMetaSet = reader.GetBoolean(5);
            var searchParamHash = reader.IsDBNull(6) ? null : reader.GetString(6);
            var requestMethod = reader.IsDBNull(7) ? null : reader.GetString(7);

            var rawResourceData = DecompressRawResource(rawBytes);
            var rawResource = new RawResource(rawResourceData, FhirResourceFormat.Json, isMetaSet);
            var request = requestMethod != null ? new ResourceRequest(requestMethod, null) : null;

            return new ResourceWrapper(
                resourceId,
                version,
                resourceType,
                rawResource,
                request,
                surrogateId.ToLastUpdated(),
                isDeleted,
                null,
                null,
                null,
                searchParamHash,
                surrogateId)
            {
                IsHistory = isHistory,
            };
        }

        private static byte[] CompressRawResource(string rawResource)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
            using (var writer = new StreamWriter(gz, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.Write(rawResource);
            }

            return ms.ToArray();
        }

        private static string DecompressRawResource(byte[] compressed)
        {
            using var ms = new MemoryStream(compressed);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var reader = new StreamReader(gz, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }
}
