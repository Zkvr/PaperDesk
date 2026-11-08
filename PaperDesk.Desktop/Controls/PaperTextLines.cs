using System.Globalization;
using System.Windows.Media.TextFormatting;

namespace PaperDesk.Desktop.Controls;

internal sealed class PaperTextLines(Typeface typeface, double fontSize, double pixelsPerDip, Brush ink)
    : TextParagraphProperties
{
    private readonly TextRunProperties run = new Run(typeface, fontSize, ink) { PixelsPerDip = pixelsPerDip };

    public override FlowDirection FlowDirection => FlowDirection.LeftToRight;
    public override TextAlignment TextAlignment => TextAlignment.Left;
    public override double LineHeight => 0;
    public override bool FirstLineInParagraph => false;
    public override TextRunProperties DefaultTextRunProperties => run;
    public override TextWrapping TextWrapping => TextWrapping.Wrap;
    public override TextMarkerProperties? TextMarkerProperties => null;
    public override double Indent => 0;

    public int GetCaretOffset(string text, double distance, PaperInline? inline = null, int start = 0)
    {
        if (text.Length == 0) return 0;
        using var formatter = TextFormatter.Create();
        var source = new Source(text, run, inline, start) { PixelsPerDip = run.PixelsPerDip };
        using var line = formatter.FormatLine(source, 0, 100_000, this, null);
        var hit = line.GetCharacterHitFromDistance(Math.Max(0, distance));
        return Math.Clamp(hit.FirstCharacterIndex + hit.TrailingLength, 0, text.Length);
    }

    public IEnumerable<(int Offset, string Text)> Wrap(TextFormatter formatter, string text, double width, PaperInline? inline = null) =>
        Wrap(formatter, text, () => width, inline);

    public IEnumerable<(int Offset, string Text)> Wrap(TextFormatter formatter, string text, Func<double> width, PaperInline? inline = null)
    {
        if (text.Length == 0)
        {
            yield return (0, "");
            yield break;
        }

        var source = new Source(text, run, inline) { PixelsPerDip = run.PixelsPerDip };
        TextLineBreak? previous = null;
        try
        {
            for (var offset = 0; offset < text.Length;)
            {
                using var line = formatter.FormatLine(source, offset, Math.Max(1, width()), this, previous);
                previous?.Dispose();
                previous = line.GetTextLineBreak();
                var length = Math.Min(text.Length - offset, line.Length);
                yield return (offset, text.Substring(offset, length));
                offset += length;
            }
        }
        finally { previous?.Dispose(); }
    }

    private sealed class Source(string text, TextRunProperties properties, PaperInline? inline = null, int start = 0) : TextSource
    {
        public override TextRun GetTextRun(int index)
        {
            if (index >= text.Length) return new TextEndOfParagraph(1);
            if (inline == null) return new TextCharacters(text, index, text.Length - index, properties);

            var part = inline.At(start + index);
            var typeface = properties.Typeface;
            if (part.Style != PaperInline.Style.Plain)
                typeface = new Typeface(part.Style.HasFlag(PaperInline.Style.Code) ? new FontFamily("Consolas") : typeface.FontFamily,
                    part.Style.HasFlag(PaperInline.Style.Italic) ? FontStyles.Italic : typeface.Style,
                    part.Style.HasFlag(PaperInline.Style.Bold) ? FontWeights.Bold : typeface.Weight, typeface.Stretch);
            var styled = typeface == properties.Typeface ? properties :
                new Run(typeface, properties.FontRenderingEmSize, properties.ForegroundBrush) { PixelsPerDip = properties.PixelsPerDip };
            return new TextCharacters(text, index, Math.Min(text.Length - index, part.Start + part.Length - start - index), styled);
        }

        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int index)
        {
            var length = Math.Min(index, text.Length);
            var buffer = new CharacterBufferRange(text, 0, length);
            return new TextSpan<CultureSpecificCharacterBufferRange>(length, new(properties.CultureInfo, buffer));
        }

        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) => index;
    }

    private sealed class Run(Typeface typeface, double fontSize, Brush ink) : TextRunProperties
    {
        public override Typeface Typeface => typeface;
        public override double FontRenderingEmSize => fontSize;
        public override double FontHintingEmSize => fontSize;
        public override TextDecorationCollection? TextDecorations => null;
        public override Brush ForegroundBrush => ink;
        public override Brush? BackgroundBrush => null;
        public override CultureInfo CultureInfo => CultureInfo.CurrentUICulture;
        public override TextEffectCollection? TextEffects => null;
    }
}
