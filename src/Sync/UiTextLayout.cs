using System;
using CupheadOnline.Net;

namespace CupheadOnline.Sync
{
    /// <summary>Text settings needed to preserve the host's layout when flattening its canvas hierarchy.</summary>
    public struct UiTextLayout
    {
        public byte HorizontalOverflow, VerticalOverflow, OverflowMode;
        public bool AlignByGeometry, Kerning, ParseControls, ExplicitLines;
        public float Aspect, CharacterSpacing, ParagraphSpacing, WidthAdjustment;
        public float Left, Top, Right, Bottom; // margins normalized to the source rect
        public int VisibleCharacters, VisibleWords, VisibleLines, Page;

        public static float FontUnits(int requestedSize, int fittedPixels, float pixelsPerUnit, bool bestFit)
        {
            // Unity's TextGenerator reports fitted size in atlas pixels, not the Text component's local units.
            return bestFit && fittedPixels > 0 && pixelsPerUnit > 0.0001f ? fittedPixels / pixelsPerUnit : requestedSize;
        }

        public static float RectAspect(float localWidth, float localHeight, float screenWidth, float screenHeight)
        {
            if (localWidth <= 0.0001f || localHeight <= 0.0001f || screenWidth <= 0.0001f || screenHeight <= 0.0001f) return 1f;
            return Math.Max(0.001f, Math.Min(1000f, (screenWidth / localWidth) / (screenHeight / localHeight)));
        }

        private static bool Near(float a, float b) { return Math.Abs(a - b) < 0.0001f; }

        public bool Same(ref UiTextLayout other)
        {
            return HorizontalOverflow == other.HorizontalOverflow && VerticalOverflow == other.VerticalOverflow &&
                OverflowMode == other.OverflowMode && AlignByGeometry == other.AlignByGeometry && Kerning == other.Kerning &&
                ParseControls == other.ParseControls && ExplicitLines == other.ExplicitLines && Near(Aspect, other.Aspect) && Near(CharacterSpacing, other.CharacterSpacing) &&
                Near(ParagraphSpacing, other.ParagraphSpacing) && Near(WidthAdjustment, other.WidthAdjustment) &&
                Near(Left, other.Left) && Near(Top, other.Top) && Near(Right, other.Right) && Near(Bottom, other.Bottom) &&
                VisibleCharacters == other.VisibleCharacters && VisibleWords == other.VisibleWords && VisibleLines == other.VisibleLines && Page == other.Page;
        }

        public void Write(NetWriter w)
        {
            w.U8(HorizontalOverflow); w.U8(VerticalOverflow); w.U8(OverflowMode);
            w.Bool(AlignByGeometry); w.Bool(Kerning); w.Bool(ParseControls); w.Bool(ExplicitLines);
            w.F32(Aspect); w.F32(CharacterSpacing); w.F32(ParagraphSpacing); w.F32(WidthAdjustment);
            w.F32(Left); w.F32(Top); w.F32(Right); w.F32(Bottom);
            w.I32(VisibleCharacters); w.I32(VisibleWords); w.I32(VisibleLines); w.I32(Page);
        }

        public void Read(NetReader r)
        {
            HorizontalOverflow = r.U8(); VerticalOverflow = r.U8(); OverflowMode = r.U8();
            AlignByGeometry = r.Bool(); Kerning = r.Bool(); ParseControls = r.Bool(); ExplicitLines = r.Bool();
            Aspect = r.F32(); CharacterSpacing = r.F32(); ParagraphSpacing = r.F32(); WidthAdjustment = r.F32();
            Left = r.F32(); Top = r.F32(); Right = r.F32(); Bottom = r.F32();
            VisibleCharacters = r.I32(); VisibleWords = r.I32(); VisibleLines = r.I32(); Page = r.I32();
        }
    }
}
