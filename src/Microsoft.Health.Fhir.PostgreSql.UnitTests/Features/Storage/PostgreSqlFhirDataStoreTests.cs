// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.PostgreSql.Features.Storage;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.PostgreSql.UnitTests.Features.Storage
{
    /// <summary>
    /// Unit tests for <see cref="PostgreSqlFhirDataStore"/>.
    /// These tests verify the public interface contract without connecting to a real database.
    /// Database-bound tests are placed in the integration test project.
    /// </summary>
    public class PostgreSqlFhirDataStoreTests
    {
        private readonly INpgsqlConnectionFactory _connectionFactory;
        private readonly PostgreSqlFhirModel _model;
        private readonly PostgreSqlFhirDataStore _store;

        public PostgreSqlFhirDataStoreTests()
        {
            _connectionFactory = Substitute.For<INpgsqlConnectionFactory>();
            _model = Substitute.ForPartsOf<PostgreSqlFhirModel>(_connectionFactory, NullLogger<PostgreSqlFhirModel>.Instance);
            _store = new PostgreSqlFhirDataStore(_connectionFactory, _model, NullLogger<PostgreSqlFhirDataStore>.Instance);
        }

        [Fact]
        public void Constructor_WhenConnectionFactoryIsNull_ThrowsArgumentNullException()
        {
            // Arrange, Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new PostgreSqlFhirDataStore(
                    null!,
                    _model,
                    NullLogger<PostgreSqlFhirDataStore>.Instance));
        }

        [Fact]
        public void Constructor_WhenModelIsNull_ThrowsArgumentNullException()
        {
            // Arrange, Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new PostgreSqlFhirDataStore(
                    _connectionFactory,
                    null!,
                    NullLogger<PostgreSqlFhirDataStore>.Instance));
        }

        [Fact]
        public void Constructor_WhenLoggerIsNull_ThrowsArgumentNullException()
        {
            // Arrange, Act & Assert
            Assert.Throws<ArgumentNullException>(() =>
                new PostgreSqlFhirDataStore(
                    _connectionFactory,
                    _model,
                    null!));
        }

        [Fact]
        public async Task GetAsync_WhenKeyIsNull_ThrowsArgumentNullException()
        {
            // Arrange, Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                _store.GetAsync((ResourceKey)null!, CancellationToken.None));
        }

        [Fact]
        public async Task GetAsync_BatchWhenKeysIsNull_ThrowsArgumentNullException()
        {
            // Arrange, Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                _store.GetAsync((System.Collections.Generic.IReadOnlyList<ResourceKey>)null!, CancellationToken.None));
        }

        [Fact]
        public async Task GetAsync_BatchWhenKeysIsEmpty_ReturnsEmptyList()
        {
            // Arrange
            var keys = System.Array.Empty<ResourceKey>();

            // Act
            var result = await _store.GetAsync(keys, CancellationToken.None);

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public async Task GetProvisionedDataStoreCapacityAsync_ReturnsNull()
        {
            // Arrange & Act
            var result = await _store.GetProvisionedDataStoreCapacityAsync(CancellationToken.None);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task TryLogEvent_DoesNotThrow()
        {
            // Arrange, Act & Assert — should complete without exception
            await _store.TryLogEvent("test-process", "test-status", "test-text", DateTime.UtcNow, CancellationToken.None);
        }

        [Fact]
        public async Task MergeAsync_WhenResourcesIsNull_ThrowsArgumentNullException()
        {
            // Arrange, Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                _store.MergeAsync(
                    (System.Collections.Generic.IReadOnlyList<ResourceWrapperOperation>)null!,
                    CancellationToken.None));
        }

        [Fact]
        public async Task MergeAsync_WhenResourcesIsEmpty_ReturnsEmptyOutcome()
        {
            // Arrange
            var resources = System.Array.Empty<ResourceWrapperOperation>();

            // Act
            var result = await _store.MergeAsync(resources, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result.Results);
        }
    }
}
