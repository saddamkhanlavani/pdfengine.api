namespace PdfEngine.Application.Common;

/// <summary>
/// Identifies the exact rendering stack that produced a PDF (Release Gate J).
///
/// The commercial problem: Chromium ships a new version, layout shifts by a hair, and
/// last month's invoice silently reflows. Nobody notices until a customer does. The gate
/// PASS condition is "same input + same engine version + same profile ⇒ materially
/// identical output", which is unverifiable unless "engine version" is a real, reportable
/// value — so it is one, returned on every render and assertable by the caller.
///
/// <see cref="Profile"/> is the render-profile revision and is bumped BY HAND whenever a
/// change to this engine can alter layout. It is deliberately not derived from the
/// assembly version: most commits (docs, API surface, logging) cannot move a glyph, and a
/// version that changes on every build would make pinning useless.
/// </summary>
public static class EngineVersion
{
    /// <summary>
    /// Render-profile revision. Bump when engine changes can alter output.
    ///
    /// History:
    ///   2026.08    initial pinned profile
    ///   2026.08.1  `@page { size: ... }` is now honored. The `size` descriptor was being
    ///              stripped by the CSS sanitizer while `margin` survived, so every
    ///              document rendered A4 regardless of the size it asked for. Fixing it
    ///              moved the A4 MediaBox from 595.28x841.89 to 594.96x841.92 (the CSS
    ///              path rather than the Format path), which changed every pinned
    ///              structural fingerprint. Callers pinning "2026.08" will now be refused
    ///              rather than silently served different geometry — that refusal is the
    ///              feature.
    /// </summary>
    public const string Profile = "2026.08.1";

    private static string _chromium = "unknown";

    /// <summary>
    /// Recorded once from the live browser. Chromium's version is the single largest
    /// source of layout drift, so an engine version that omitted it would be a promise
    /// the engine cannot keep.
    /// </summary>
    public static void SetChromiumVersion(string version)
    {
        if (!string.IsNullOrWhiteSpace(version)) _chromium = version.Trim();
    }

    /// <summary>e.g. <c>2026.08+chromium133.0.6943.16</c>.</summary>
    public static string Current => $"{Profile}+chromium{_chromium}";

    public static string ChromiumVersion => _chromium;
}
