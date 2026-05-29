// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Fhir.Core.Extensions;

namespace Microsoft.Health.Fhir.PostgreSql.Features.Storage
{
    /// <summary>
    /// The resource surrogate ID encodes the LastModified timestamp as a left-shifted 100-ns-tick
    /// bigint (matching the SQL Server implementation). This helper provides the same encoding so
    /// that surrogate IDs are comparable across both storage backends.
    /// </summary>
    internal static class ResourceSurrogateIdHelper
    {
        internal static long ToSurrogateId(this DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.ToId();
        }

        internal static DateTimeOffset ToLastUpdated(this long resourceSurrogateId)
        {
            return resourceSurrogateId.ToDate();
        }
    }
}
