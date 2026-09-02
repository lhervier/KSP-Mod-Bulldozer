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

        // Sized to its content rather than to a mockup: two checkbox rows, the log level combo and the
        // quads readout. The width is set by the readout's longest line ("largest mask ... Ko total"),
        // the height by its dozen lines — the rows above would fit in far less.
        public const float WindowWidth = 380f;
        public const float WindowHeight = 360f;

        // ==============================================================
        // Content
        // ==============================================================
        public const float ContentPaddingH = 10f;
        public const float ContentPaddingV = 10f;
        public const float ContentSpacing = 8f;

        // ==============================================================
        // Quads readout
        // ==============================================================

        // One step below the labels around it: the readout is a column of aligned numbers, and its
        // longest lines have to fit the window width without wrapping to stay readable as a table.
        public const int ReadoutFontSize = 11;
        public const float ReadoutSpacing = 4f;

        // What the readout needs at its longest (a body, four quads, a blade previewing). Below that the
        // window would clip lines rather than shrink, since the text is truncated and not scrolled.
        public const float ReadoutMinHeight = 190f;
    }
}
