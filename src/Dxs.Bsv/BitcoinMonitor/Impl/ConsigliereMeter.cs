using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;

using Dxs.Bsv.BitcoinMonitor.Models;

namespace Dxs.Bsv.BitcoinMonitor.Impl;

/// <summary>
/// OpenTelemetry-compatible metrics surface for the transaction filter, built on
/// the BCL <see cref="System.Diagnostics.Metrics.Meter"/> API (no OTel package
/// dependency in <c>Dxs.Bsv</c> — the exporter is wired in the host).
///
/// This sits alongside the existing internal <see cref="TransactionFilterMetrics"/>
/// (which the filter logs + resets every minute); these instruments are the
/// monotonic, scrape-able view. The meter name <see cref="MeterName"/> is what
/// the host's <c>AddMeter(...)</c> subscribes to.
///
/// Counters (cumulative): transactions screened, and a per-status saved-tx
/// counter tagged by <c>status</c>. Gauges (observable): the current watched
/// address / token / Glyph-ref counts, read on each collection via callbacks the
/// filter registers through <see cref="SetWatchGauges"/>.
/// </summary>
public sealed class ConsigliereMeter : IDisposable
{
    public const string MeterName = "ConsigliereRXD.TransactionFilter";

    private readonly Meter _meter;
    private readonly Counter<long> _screened;
    private readonly Counter<long> _saved;

    private Func<int> _watchedAddresses = static () => 0;
    private Func<int> _watchedTokens = static () => 0;
    private Func<int> _watchedGlyphRefs = static () => 0;

    public ConsigliereMeter()
    {
        _meter = new Meter(MeterName);

        _screened = _meter.CreateCounter<long>(
            "consigliere_tx_screened_total",
            unit: "{transaction}",
            description: "Transactions evaluated by the filter (matched or not).");

        _saved = _meter.CreateCounter<long>(
            "consigliere_tx_saved_total",
            unit: "{transaction}",
            description: "Matched transactions persisted, tagged by process status.");

        // Gauges are long-typed (callbacks return long) so any MeterListener /
        // OTel exporter subscribing with the long measurement callback receives
        // them — an int-typed observable would be silently skipped by a long-only
        // listener.
        _meter.CreateObservableGauge<long>(
            "consigliere_watched_addresses",
            () => _watchedAddresses(),
            description: "Currently watched P2PKH addresses.");

        _meter.CreateObservableGauge<long>(
            "consigliere_watched_tokens",
            () => _watchedTokens(),
            description: "Currently watched BSV STAS/DSTAS token ids.");

        _meter.CreateObservableGauge<long>(
            "consigliere_watched_glyph_refs",
            () => _watchedGlyphRefs(),
            description: "Currently watched Radiant Glyph token refs.");
    }

    /// <summary>Increment the count of transactions screened by the filter.</summary>
    public void RecordScreened() => _screened.Add(1);

    /// <summary>Record a saved transaction, tagged with its process status.</summary>
    public void RecordSaved(TransactionProcessStatus status)
        => _saved.Add(1, new KeyValuePair<string, object>("status", status.ToString()));

    /// <summary>
    /// Wire the observable-gauge callbacks to the live watch-set counters. Called
    /// once by the filter after its watch set is constructed.
    /// </summary>
    public void SetWatchGauges(Func<int> addresses, Func<int> tokens, Func<int> glyphRefs)
    {
        if (addresses != null) _watchedAddresses = addresses;
        if (tokens != null) _watchedTokens = tokens;
        if (glyphRefs != null) _watchedGlyphRefs = glyphRefs;
    }

    public void Dispose() => _meter.Dispose();
}
