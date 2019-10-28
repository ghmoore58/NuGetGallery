﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHubVulnerabilities2Db.GraphQL;
using NuGet.Services.Cursor;

namespace GitHubVulnerabilities2Db.Collector
{
    public class AdvisoryCollectorQueryService : IAdvisoryCollectorQueryService
    {
        public AdvisoryCollectorQueryService(
            IQueryService queryService,
            IAdvisoryCollectorQueryBuilder queryBuilder)
        {
            _queryService = queryService ?? throw new ArgumentNullException(nameof(queryService));
            _queryBuilder = queryBuilder ?? throw new ArgumentNullException(nameof(queryBuilder));
        }

        private readonly IQueryService _queryService;
        private readonly IAdvisoryCollectorQueryBuilder _queryBuilder;

        public async Task<IReadOnlyList<SecurityAdvisory>> GetAdvisoriesSinceAsync(ReadCursor<DateTimeOffset> cursor, CancellationToken token)
        {
            await cursor.Load(token);
            var firstQuery = _queryBuilder.CreateSecurityAdvisoriesQuery(updatedSince: cursor.Value);
            var firstResponse = await _queryService.QueryAsync(firstQuery, token);
            var lastAdvisoryEdges = firstResponse?.Data?.SecurityAdvisories?.Edges?.ToList() ?? Enumerable.Empty<Edge<SecurityAdvisory>>();
            var advisories = lastAdvisoryEdges.Select(e => e.Node).ToList();
            while (lastAdvisoryEdges.Any())
            {
                var nextQuery = _queryBuilder.CreateSecurityAdvisoriesQuery(afterCursor: lastAdvisoryEdges.Last().Cursor);
                var nextResponse = await _queryService.QueryAsync(nextQuery, token);
                lastAdvisoryEdges = nextResponse?.Data?.SecurityAdvisories?.Edges?.ToList() ?? Enumerable.Empty<Edge<SecurityAdvisory>>();
                advisories.AddRange(lastAdvisoryEdges.Select(e => e.Node));
            }

            return await Task.WhenAll(advisories.Select(a => FetchAllVulnerabilities(a, token)));
        }

        private async Task<SecurityAdvisory> FetchAllVulnerabilities(SecurityAdvisory advisory, CancellationToken token)
        {
            // If the last time we fetched this advisory, it returned the maximum amount of vulnerabilities, query again to fetch the next batch.
            var lastVulnerabilitiesFetchedCount = advisory.Vulnerabilities?.Edges?.Count() ?? 0;
            while (lastVulnerabilitiesFetchedCount == _queryBuilder.GetMaximumResultsPerRequest())
            {
                var queryForAdditionalVulnerabilities = _queryBuilder.CreateSecurityAdvisoryQuery(advisory);
                var responseForAdditionalVulnerabilities = await _queryService.QueryAsync(queryForAdditionalVulnerabilities, token);
                var advisoryWithAdditionalVulnerabilities = responseForAdditionalVulnerabilities.Data.SecurityAdvisory;
                lastVulnerabilitiesFetchedCount = advisoryWithAdditionalVulnerabilities.Vulnerabilities?.Edges?.Count() ?? 0;
                advisory = MergeAdvisories(advisory, advisoryWithAdditionalVulnerabilities);
            }

            // We have seen some duplicate ranges (same ID and version range) returned by the API before, so make sure to dedupe the ranges.
            var comparer = new VulnerabilityForSameAdvisoryComparer();
            if (advisory.Vulnerabilities?.Edges != null)
            {
                advisory.Vulnerabilities.Edges = advisory.Vulnerabilities.Edges.Distinct(comparer);
            }

            return advisory;
        }

        private SecurityAdvisory MergeAdvisories(SecurityAdvisory advisory, SecurityAdvisory nextAdvisory)
        {
            // We want to keep the next advisory's data, but prepend the existing vulnerabilities that were returned in previous queries.
            nextAdvisory.Vulnerabilities.Edges = advisory.Vulnerabilities.Edges.Concat(
                nextAdvisory.Vulnerabilities.Edges ?? Enumerable.Empty<Edge<SecurityVulnerability>>());
            // We are not querying the advisories feed at this time so we do not want to advance the advisory cursor past what it was originally.
            nextAdvisory.UpdatedAt = advisory.UpdatedAt;
            return nextAdvisory;
        }

        private class VulnerabilityForSameAdvisoryComparer : IEqualityComparer<SecurityVulnerability>, IEqualityComparer<Edge<SecurityVulnerability>>
        {
            public bool Equals(SecurityVulnerability x, SecurityVulnerability y)
            {
                return x?.Package?.Name == y?.Package?.Name
                    && x?.VulnerableVersionRange == y?.VulnerableVersionRange;
            }

            public bool Equals(Edge<SecurityVulnerability> x, Edge<SecurityVulnerability> y)
            {
                return Equals(x?.Node, y?.Node);
            }

            public int GetHashCode(SecurityVulnerability obj)
            {
                return Tuple
                    .Create(
                        obj?.Package?.Name, 
                        obj?.VulnerableVersionRange)
                    .GetHashCode();
            }

            public int GetHashCode(Edge<SecurityVulnerability> obj)
            {
                return GetHashCode(obj?.Node);
            }
        }
    }
}
