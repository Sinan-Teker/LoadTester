namespace LoadTester.Web.Data;

public sealed class ScenarioEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Serialized <see cref="LoadTester.Core.Scenarios.ScenarioDefinition"/>.</summary>
    public string DefinitionJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum RunStatus
{
    Running,
    Completed,
    Stopped,
    Failed,

    /// <summary>The application stopped while the run was in progress.</summary>
    Interrupted,
}

public sealed class TestRunEntity
{
    public int Id { get; set; }
    public int ScenarioId { get; set; }
    public string ScenarioName { get; set; } = "";

    /// <summary>The scenario as it was when the run started.</summary>
    public string DefinitionJson { get; set; } = "{}";

    public RunStatus Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }

    // Summary columns so the history list does not have to load the JSON documents.
    public double DurationSeconds { get; set; }
    public long TotalRequests { get; set; }
    public long FailedRequests { get; set; }
    public double RequestsPerSecond { get; set; }
    public double? P95Ms { get; set; }
    public int PeakVirtualUsers { get; set; }

    /// <summary>Serialized <see cref="LoadTester.Core.Metrics.RunReport"/>.</summary>
    public string? ReportJson { get; set; }

    /// <summary>Serialized per-second <see cref="LoadTester.Core.Metrics.TimePoint"/> list.</summary>
    public string? TimelineJson { get; set; }

    public string? Error { get; set; }
}
