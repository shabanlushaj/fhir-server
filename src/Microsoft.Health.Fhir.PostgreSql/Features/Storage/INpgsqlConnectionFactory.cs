// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Microsoft.Health.Fhir.PostgreSql.Features.Storage
{
    /// <summary>
    /// Factory that creates and opens <see cref="NpgsqlConnection"/> instances.
    /// </summary>
    public interface INpgsqlConnectionFactory
    {
        /// <summary>
        /// Opens and returns a ready-to-use <see cref="NpgsqlConnection"/>.
        /// </summary>
        Task<NpgsqlConnection> GetConnectionAsync(CancellationToken cancellationToken);
    }
}
