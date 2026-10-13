namespace WhyNotSleeping.Gui;

/// <summary>
/// Muted neutral surfaces with color kept for the verdicts. Dark mirrors Windows' own dark surfaces; the accents are
/// lightened there so they keep their contrast.
/// </summary>
internal sealed record Palette(
    Color Background, Color Surface, Color Text, Color Muted,
    Color Red, Color Amber, Color Green, Color Purple, Color Blue,
    Color BannerBack, Color BannerText)
{
    public static readonly Palette Light = new(
        Background: Color.FromArgb(226, 228, 232),
        Surface: Color.FromArgb(240, 241, 243),
        Text: Color.FromArgb(36, 38, 42),
        Muted: Color.FromArgb(98, 103, 110),
        Red: Color.FromArgb(172, 50, 40),
        Amber: Color.FromArgb(150, 96, 10),
        Green: Color.FromArgb(32, 112, 64),
        Purple: Color.FromArgb(110, 70, 150),
        Blue: Color.FromArgb(40, 95, 150),
        BannerBack: Color.FromArgb(234, 226, 204),
        BannerText: Color.FromArgb(92, 70, 20));

    public static readonly Palette Dark = new(
        Background: Color.FromArgb(32, 32, 32),
        Surface: Color.FromArgb(44, 44, 44),
        Text: Color.FromArgb(228, 228, 228),
        Muted: Color.FromArgb(158, 158, 158),
        Red: Color.FromArgb(235, 120, 108),
        Amber: Color.FromArgb(226, 178, 86),
        Green: Color.FromArgb(112, 196, 138),
        Purple: Color.FromArgb(186, 152, 232),
        Blue: Color.FromArgb(120, 170, 225),
        BannerBack: Color.FromArgb(68, 58, 34),
        BannerText: Color.FromArgb(238, 214, 152));

    public static Color Blend(Color background, Color accent, double amount) => Color.FromArgb(
        (int)(background.R + (accent.R - background.R) * amount),
        (int)(background.G + (accent.G - background.G) * amount),
        (int)(background.B + (accent.B - background.B) * amount));
}
