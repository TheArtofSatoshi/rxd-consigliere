using System.ComponentModel.DataAnnotations;

namespace Dxs.Consigliere.Dto.Requests;

/// <summary>
/// Request to watch a Radiant Glyph token by its induction ref (compact outpoint
/// hex: 32-byte txid LE + 4-byte vout LE = 72 hex chars).
/// </summary>
public record WatchGlyphRefRequest([Required] string GlyphRef, string Name = null);
