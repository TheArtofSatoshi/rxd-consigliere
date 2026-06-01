using Dxs.Bsv.BitcoinMonitor.Impl;
using Dxs.Consigliere.Configs;
using Dxs.Consigliere.Services.Metrics;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using OpenTelemetry.Metrics;

namespace Dxs.Consigliere.Setup;

/// <summary>
/// Wave 4 S7 — DI wiring for the source-metrics aggregator, plus the
/// OpenTelemetry meter + Prometheus scrape endpoint (opt-in via
/// <c>Consigliere:Metrics:OpenTelemetry:Enabled</c>).
///
/// Registered from <c>Startup.ConfigureServices</c> after the W2 + W3 zones (the
/// collector consumes <c>SourceObservationRecorder</c>,
/// <c>OrphanedTxRebroadcastRecorder</c>, <c>BsvP2pHealth</c>).
/// </summary>
public static class MetricsSetup
{
    public static IServiceCollection AddMetricsZoneServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .Configure<SourceMetricsConfig>(configuration.GetSection("Consigliere:Metrics:Sources"))
            .AddSingleton(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<SourceMetricsConfig>>().Value;
                return new SourceVisibilityTracker(new SourceVisibilityTrackerOptions
                {
                    EvictionWindowMs = opts.EvictionWindowMs,
                });
            })
            .AddSingleton<ISourceMetricsCollector, SourceMetricsCollector>()
            .AddSingleton<ISnapshotPersistence, RavenSnapshotPersistence>()
            .AddSingleton<SourceMetricsAggregator>()
            .AddHostedService(sp => sp.GetRequiredService<SourceMetricsAggregator>());

        // The Meter is registered unconditionally so the TransactionFilter can
        // resolve + record into it; instruments cost nothing when no
        // MeterProvider listens. The Prometheus exporter is opt-in.
        services
            .Configure<MetricsEndpointConfig>(configuration.GetSection("Consigliere:Metrics:OpenTelemetry"))
            .AddSingleton<ConsigliereMeter>();

        var metricsConfig = new MetricsEndpointConfig();
        configuration.GetSection("Consigliere:Metrics:OpenTelemetry").Bind(metricsConfig);

        if (metricsConfig.Enabled)
        {
            services.AddOpenTelemetry().WithMetrics(m =>
            {
                m.AddMeter(ConsigliereMeter.MeterName);
                if (metricsConfig.IncludeAspNetCoreInstrumentation)
                    m.AddAspNetCoreInstrumentation();
                if (metricsConfig.IncludeRuntimeInstrumentation)
                    m.AddRuntimeInstrumentation();
                m.AddPrometheusExporter();
            });
        }

        return services;
    }
}
