// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using EnsureThat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Registration;
using Microsoft.Health.Fhir.PostgreSql.Features.Storage;
using Microsoft.Health.Fhir.PostgreSql.Registration;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Extension methods for registering the PostgreSQL FHIR data store with the DI container.
    /// </summary>
    public static class FhirServerBuilderPostgreSqlRegistrationExtensions
    {
        /// <summary>
        /// Adds the PostgreSQL storage provider to the FHIR server.
        /// </summary>
        /// <param name="fhirServerBuilder">The FHIR server builder.</param>
        /// <param name="configureAction">Optional configuration action for <see cref="PostgreSqlDataStoreConfiguration"/>.</param>
        /// <returns>The same <see cref="IFhirServerBuilder"/> for chaining.</returns>
        public static IFhirServerBuilder AddPostgreSql(
            this IFhirServerBuilder fhirServerBuilder,
            Action<PostgreSqlDataStoreConfiguration> configureAction = null)
        {
            EnsureArg.IsNotNull(fhirServerBuilder, nameof(fhirServerBuilder));

            var services = fhirServerBuilder.Services;

            // Bind configuration
            if (configureAction != null)
            {
                services.Configure(configureAction);
            }
            else
            {
                services.AddOptions<PostgreSqlDataStoreConfiguration>()
                    .Configure<IConfiguration>((opts, config) =>
                        config.GetSection("FhirPostgreSql").Bind(opts));
            }

            // Connection factory (singleton — wraps an NpgsqlDataSource connection pool)
            services.Add<NpgsqlConnectionFactory>()
                .Singleton()
                .AsSelf()
                .AsService<INpgsqlConnectionFactory>();

            // In-memory model (singleton)
            services.Add<PostgreSqlFhirModel>()
                .Singleton()
                .AsSelf();

            // Scoped data stores — replaces any previously registered implementations
            services.Add<PostgreSqlFhirDataStore>()
                .Scoped()
                .AsSelf()
                .ReplaceService<IFhirDataStore>();

            services.Add<PostgreSqlFhirOperationDataStore>()
                .Scoped()
                .AsSelf()
                .ReplaceService<IFhirOperationDataStore>();

            // Schema initializer runs on startup
            services.Add<PostgreSqlSchemaInitializer>()
                .Singleton()
                .AsSelf()
                .AsService<IHostedService>();

            return fhirServerBuilder;
        }
    }
}
