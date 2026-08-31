namespace com.github.lhervier.ksp.bulldozermod.ui.styles
{
    /// <summary>
    /// Sizes and colors proper to the Bulldozer debug window. Anything already shared (paddings, label
    /// color, checkbox metrics) comes from the KSP-Shared palettes instead: no builder of this mod
    /// hard-codes a dimension.
    /// </summary>
    public static class BulldozerPalette
    {
        // ==============================================================
        // Main window
        // ==============================================================

        // Sized to its content rather than to a mockup: two checkbox rows, with room for the labels to
        // breathe and for a third one to be added without reopening this file.
        public const float WindowWidth = 320f;
        public const float WindowHeight = 110f;

        // ==============================================================
        // Content
        // ==============================================================
        public const float ContentPaddingH = 10f;
        public const float ContentPaddingV = 10f;
        public const float ContentSpacing = 8f;
    }
}
