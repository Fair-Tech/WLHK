namespace Wlhk.UI;

/// <summary>Palette matching the v1 web UI (light/dark via system preference).</summary>
public sealed class Theme
{
    public Color Bg, Text, Card, Border, Primary, PrimaryHover, Danger, Subtle;
    public Color BannerBg, BannerBorder, BannerText;
    public Color Connected = Color.FromArgb(16, 124, 16);

    public static Theme Current()
    {
        bool dark = Application.IsDarkModeEnabled;
        return dark
            ? new Theme
            {
                Bg = Color.FromArgb(32, 32, 32),
                Text = Color.White,
                Card = Color.FromArgb(45, 45, 45),
                Border = Color.FromArgb(61, 61, 61),
                Primary = Color.FromArgb(0, 120, 212),
                PrimaryHover = Color.FromArgb(0, 106, 188),
                Danger = Color.FromArgb(209, 52, 56),
                Subtle = Color.FromArgb(160, 160, 160),
                BannerBg = Color.FromArgb(59, 46, 0),
                BannerBorder = Color.FromArgb(160, 120, 0),
                BannerText = Color.FromArgb(255, 217, 102),
                Connected = Color.FromArgb(108, 203, 95)
            }
            : new Theme
            {
                Bg = Color.FromArgb(243, 243, 243),
                Text = Color.FromArgb(32, 32, 32),
                Card = Color.White,
                Border = Color.FromArgb(229, 229, 229),
                Primary = Color.FromArgb(0, 120, 212),
                PrimaryHover = Color.FromArgb(0, 106, 188),
                Danger = Color.FromArgb(209, 52, 56),
                Subtle = Color.FromArgb(110, 110, 110),
                BannerBg = Color.FromArgb(255, 244, 206),
                BannerBorder = Color.FromArgb(240, 192, 64),
                BannerText = Color.FromArgb(93, 67, 0)
            };
    }
}
