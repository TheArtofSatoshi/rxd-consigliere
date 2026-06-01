namespace Dxs.Consigliere.Configs;

/// <summary>
/// OpenTelemetry metrics-export configuration, bound from
/// <c>Consigliere:Metrics:OpenTelemetry</c>. Default **disabled** so the metrics
/// surface is opt-in and existing deployments are unaffected. When enabled, the
/// host wires a <c>MeterProvider</c> (subscribing to the Consigliere meters +
/// ASP.NET Core / runtime instrumentation) and exposes a Prometheus scrape
/// endpoint at <see cref="ScrapeEndpointPath"/>.
/// </summary>
public sealed class MetricsEndpointConfig
{
    /// <summary>Master switch. Default <c>false</c>.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Prometheus scrape path. Default <c>/metrics</c>.</summary>
    public string ScrapeEndpointPath { get; set; } = "/metrics";

    /// <summary>Include ASP.NET Core HTTP server instrumentation. Default true.</summary>
    public bool IncludeAspNetCoreInstrumentation { get; set; } = true;

    /// <summary>Include .NET runtime instrumentation (GC, threadpool, etc). Default true.</summary>
    public bool IncludeRuntimeInstrumentation { get; set; } = true;
}
