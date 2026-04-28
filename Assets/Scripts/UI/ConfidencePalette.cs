using UnityEngine;

namespace SemanticXR.UI
{
    // Confidence color palette used by the server when assigning per-cluster
    // colors and mirrored here so the HUD legend and the S3 popup stay
    // identical to what the user sees on the points.
    //
    // Mirrors the server-side `_RDYLGN_WIDE_STOPS` palette: red (low confidence)
    // through yellow to deep green (high confidence).
    public static class ConfidencePalette
    {
        public static readonly Color[] Stops =
        {
            Hex("#7F0000"), // deep maroon  — lowest confidence
            Hex("#E63946"), // red
            Hex("#FF8C42"), // orange
            Hex("#FFD23F"), // yellow
            Hex("#9DD843"), // lime
            Hex("#2A9D4F"), // green
            Hex("#0D5A1C"), // deep forest — highest confidence
        };

        static Color Hex(string s)
        {
            ColorUtility.TryParseHtmlString(s, out var c);
            return c;
        }
    }
}