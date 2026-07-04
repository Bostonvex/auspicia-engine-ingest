using System.Collections.Generic;

namespace Auspicia.Engine;

/// <summary>One portfolio item for POST /xray/portfolios:bulk.</summary>
public sealed record XrayPortfolioImport
{
    public string? Name { get; init; }
    public string Source { get; init; } = "desk";
    public string? AllocationsCsv { get; init; }
    public string? PerformanceCsv { get; init; }
    public string? InvestorPortfolioId { get; init; }
}

/// <summary>Bulk Portfolio X-ray import request.</summary>
public sealed record XrayBulkImportRequest
{
    public required IReadOnlyList<XrayPortfolioImport> Portfolios { get; init; }
    public string? TargetOrgId { get; init; }
}

/// <summary>Organization that the authenticated caller may target for ingestion.</summary>
public sealed record XrayIngestionTargetOrg
{
    public string? Id { get; init; }
    public string? DisplayName { get; init; }
    public string? Status { get; init; }
    public string? Role { get; init; }
}

/// <summary>Response from GET /orgs/ingestion-targets.</summary>
public sealed record XrayIngestionTargetsResult
{
    public IReadOnlyList<XrayIngestionTargetOrg> Orgs { get; init; } = [];
    public string? DefaultOrgId { get; init; }
}

/// <summary>Bulk Portfolio X-ray import response. A 207 Multi-Status response is still represented here.</summary>
public sealed record XrayBulkImportResult
{
    public IReadOnlyList<XrayImportedItem> Imported { get; init; } = [];
    public IReadOnlyList<XrayItemError> Errors { get; init; } = [];
    public int Count { get; init; }
    public int Failed { get; init; }
    public bool HasFailures => Failed > 0 || Errors.Count > 0;
}

public sealed record XrayImportedItem
{
    public int Index { get; init; }
    public XrayPortfolio? Portfolio { get; init; }
    public XrayParseReport? ParseReport { get; init; }
}

public sealed record XrayItemError
{
    public int Index { get; init; }
    public int Status { get; init; }
    public string? Detail { get; init; }
}

public sealed record XrayPortfolio
{
    public string? Id { get; init; }
    public string? OrgId { get; init; }
    public string? CreatedBy { get; init; }
    public string? Name { get; init; }
    public string? Source { get; init; }
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public bool HasAllocations { get; init; }
    public bool HasPerformance { get; init; }
    public string? InvestorPortfolioId { get; init; }
    public string? CreatedAt { get; init; }
}

public sealed record XrayParseReport
{
    public int AllocRows { get; init; }
    public int PerfRows { get; init; }
    public XrayDateRange? DateRange { get; init; }
    public XrayTickerCoverage? Tickers { get; init; }
    public IReadOnlyList<string>? Warnings { get; init; }
    public double? GrossMax { get; init; }
    public bool CashColumn { get; init; }
}

public sealed record XrayDateRange
{
    public string? Start { get; init; }
    public string? End { get; init; }
}

public sealed record XrayTickerCoverage
{
    public IReadOnlyList<string>? Known { get; init; }
    public IReadOnlyList<string>? Unknown { get; init; }
}

/// <summary>Request body for POST /xray/portfolios/{portfolioId}/analyses.</summary>
public sealed record XrayAnalysisRequest
{
    public double? ThresholdPct { get; init; }
    public int? TopN { get; init; }
}

public sealed record XrayStartAnalysisResult
{
    public string? AnalysisId { get; init; }
    public XrayAnalysis? Analysis { get; init; }
}

public sealed record XrayAnalysis
{
    public string? Id { get; init; }
    public string? PortfolioId { get; init; }
    public string? Status { get; init; }
    public string? Stage { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public string? CompletedAt { get; init; }
    public IReadOnlyList<XrayAnalysisEvent>? Events { get; init; }
    public IReadOnlyList<XrayEpisode>? Episodes { get; init; }
}

public sealed record XrayAnalysisEvent
{
    public int Id { get; init; }
    public string? AnalysisId { get; init; }
    public string? Stage { get; init; }
    public string? Status { get; init; }
    public string? Ticker { get; init; }
    public string? Message { get; init; }
    public string? CreatedAt { get; init; }
}

public sealed record XrayEpisode
{
    public int Id { get; init; }
    public string? AnalysisId { get; init; }
    public int Idx { get; init; }
    public string? Kind { get; init; }
    public string? PeakDate { get; init; }
    public string? TroughDate { get; init; }
    public string? RecoveryDate { get; init; }
    public double DepthPct { get; init; }
}
