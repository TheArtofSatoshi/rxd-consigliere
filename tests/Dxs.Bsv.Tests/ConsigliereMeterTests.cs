using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

using Dxs.Bsv.BitcoinMonitor.Impl;
using Dxs.Bsv.BitcoinMonitor.Models;

using Xunit;

namespace Dxs.Bsv.Tests;

/// <summary>
/// Verifies the OTel meter emits real values through a <see cref="MeterListener"/>
/// (the same mechanism an OpenTelemetry MeterProvider uses), so the
/// Prometheus-exported instruments carry actual signal rather than being empty.
/// </summary>
public class ConsigliereMeterTests
{
    private static MeterListener ListenTo(
        string meterName,
        Dictionary<string, long> counterTotals,
        Dictionary<string, long> gaugeValues)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == meterName)
                    l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            // Gauge measurements arrive on RecordObservableInstruments();
            // counter measurements arrive synchronously on Add().
            if (instrument is ObservableInstrument<long>)
                gaugeValues[instrument.Name] = value;
            else
                counterTotals[instrument.Name] = counterTotals.GetValueOrDefault(instrument.Name) + value;
        });
        listener.Start();
        return listener;
    }

    [Fact]
    public void Counters_RecordScreenedAndSaved()
    {
        var counters = new Dictionary<string, long>();
        var gauges = new Dictionary<string, long>();
        using var listener = ListenTo(ConsigliereMeter.MeterName, counters, gauges);

        using var meter = new ConsigliereMeter();
        meter.RecordScreened();
        meter.RecordScreened();
        meter.RecordScreened();
        meter.RecordSaved(TransactionProcessStatus.FoundInBlock);
        meter.RecordSaved(TransactionProcessStatus.FoundInMempool);

        Assert.Equal(3, counters["consigliere_tx_screened_total"]);
        Assert.Equal(2, counters["consigliere_tx_saved_total"]);
    }

    [Fact]
    public void Gauges_ReflectWatchSetCallbacks()
    {
        var counters = new Dictionary<string, long>();
        var gauges = new Dictionary<string, long>();
        using var listener = ListenTo(ConsigliereMeter.MeterName, counters, gauges);

        using var meter = new ConsigliereMeter();
        var addresses = 7;
        var tokens = 0;
        var glyphRefs = 3;
        meter.SetWatchGauges(() => addresses, () => tokens, () => glyphRefs);

        listener.RecordObservableInstruments();

        Assert.Equal(7, gauges["consigliere_watched_addresses"]);
        Assert.Equal(0, gauges["consigliere_watched_tokens"]);
        Assert.Equal(3, gauges["consigliere_watched_glyph_refs"]);

        // Gauges are read live: changing the underlying value reflects on re-poll.
        addresses = 10;
        glyphRefs = 5;
        listener.RecordObservableInstruments();
        Assert.Equal(10, gauges["consigliere_watched_addresses"]);
        Assert.Equal(5, gauges["consigliere_watched_glyph_refs"]);
    }

    [Fact]
    public void GaugesDefaultToZero_BeforeCallbacksWired()
    {
        var counters = new Dictionary<string, long>();
        var gauges = new Dictionary<string, long>();
        using var listener = ListenTo(ConsigliereMeter.MeterName, counters, gauges);

        using var meter = new ConsigliereMeter();
        listener.RecordObservableInstruments();

        Assert.Equal(0, gauges["consigliere_watched_addresses"]);
        Assert.Equal(0, gauges["consigliere_watched_glyph_refs"]);
    }
}
