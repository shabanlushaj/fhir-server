// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.PostgreSql.Registration
{
    /// <summary>
    /// Configuration settings for the PostgreSQL FHIR data store.
    /// </summary>
    public class PostgreSqlDataStoreConfiguration
    {
        /// <summary>
        /// Gets or sets the PostgreSQL connection string.
        /// </summary>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of connections in the pool.
        /// </summary>
        public int MaxPoolSize { get; set; } = 100;

        /// <summary>
        /// Gets or sets the command timeout in seconds.
        /// </summary>
        public int CommandTimeoutSeconds { get; set; } = 300;

        /// <summary>
        /// Gets or sets the maximum number of retries on transient failures.
        /// </summary>
        public int MaxRetryCount { get; set; } = 3;
    }
}
