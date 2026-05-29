// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Health.Fhir.PostgreSql.Features.Storage;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.PostgreSql.UnitTests.Features.Storage
{
    /// <summary>
    /// Unit tests for <see cref="PostgreSqlFhirModel"/>.
    /// </summary>
    public class PostgreSqlFhirModelTests
    {
        private readonly INpgsqlConnectionFactory _connectionFactory;

        public PostgreSqlFhirModelTests()
        {
            _connectionFactory = Substitute.For<INpgsqlConnectionFactory>();
        }

        [Fact]
        public void TryGetResourceTypeId_WhenNotInitialized_ReturnsFalse()
        {
            // Arrange
            var model = new PostgreSqlFhirModel(_connectionFactory, NullLogger<PostgreSqlFhirModel>.Instance);

            // Act
            var found = model.TryGetResourceTypeId("Patient", out var id);

            // Assert
            Assert.False(found);
            Assert.Equal(0, id);
        }

        [Fact]
        public void TryGetSearchParamId_WhenNotInitialized_ReturnsFalse()
        {
            // Arrange
            var model = new PostgreSqlFhirModel(_connectionFactory, NullLogger<PostgreSqlFhirModel>.Instance);

            // Act
            var found = model.TryGetSearchParamId("http://hl7.org/fhir/SearchParameter/Resource-id", out var id);

            // Assert
            Assert.False(found);
            Assert.Equal(0, id);
        }

        [Fact]
        public void TryGetResourceTypeId_WhenNameIsNullOrEmpty_ReturnsFalse()
        {
            // Arrange
            var model = new PostgreSqlFhirModel(_connectionFactory, NullLogger<PostgreSqlFhirModel>.Instance);

            // Act & Assert
            Assert.False(model.TryGetResourceTypeId(null!, out _));
            Assert.False(model.TryGetResourceTypeId(string.Empty, out _));
        }

        [Fact]
        public void TryGetSearchParamId_WhenUriIsNullOrEmpty_ReturnsFalse()
        {
            // Arrange
            var model = new PostgreSqlFhirModel(_connectionFactory, NullLogger<PostgreSqlFhirModel>.Instance);

            // Act & Assert
            Assert.False(model.TryGetSearchParamId(null!, out _));
            Assert.False(model.TryGetSearchParamId(string.Empty, out _));
        }

        [Fact]
        public async Task GetOrCreateSystemIdAsync_WhenSystemIsNull_ReturnsZero()
        {
            // Arrange
            var model = new PostgreSqlFhirModel(_connectionFactory, NullLogger<PostgreSqlFhirModel>.Instance);

            // Act
            var id = await model.GetOrCreateSystemIdAsync(null!, CancellationToken.None);

            // Assert
            Assert.Equal(0, id);
        }

        [Fact]
        public async Task GetOrCreateQuantityCodeIdAsync_WhenCodeIsNull_ReturnsZero()
        {
            // Arrange
            var model = new PostgreSqlFhirModel(_connectionFactory, NullLogger<PostgreSqlFhirModel>.Instance);

            // Act
            var id = await model.GetOrCreateQuantityCodeIdAsync(null!, CancellationToken.None);

            // Assert
            Assert.Equal(0, id);
        }
    }
}
