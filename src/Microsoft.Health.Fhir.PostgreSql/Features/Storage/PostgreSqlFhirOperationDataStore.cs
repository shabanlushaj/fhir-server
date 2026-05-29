// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.JobManagement;

namespace Microsoft.Health.Fhir.PostgreSql.Features.Storage
{
    /// <summary>
    /// PostgreSQL-backed implementation of <see cref="IFhirOperationDataStore"/>.
    /// Delegates queue-based job management to <see cref="FhirOperationDataStoreBase"/>
    /// which uses the shared <see cref="IQueueClient"/>.
    /// </summary>
    public class PostgreSqlFhirOperationDataStore : FhirOperationDataStoreBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PostgreSqlFhirOperationDataStore"/> class.
        /// </summary>
        /// <param name="queueClient">Queue client for async job management.</param>
        /// <param name="loggerFactory">Logger factory.</param>
        public PostgreSqlFhirOperationDataStore(IQueueClient queueClient, ILoggerFactory loggerFactory)
            : base(EnsureArg.IsNotNull(queueClient, nameof(queueClient)), EnsureArg.IsNotNull(loggerFactory, nameof(loggerFactory)))
        {
        }
    }
}
