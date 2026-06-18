using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using ShanlianVpn.Windows.Models;
using MediaColor = System.Windows.Media.Color;

namespace ShanlianVpn.Windows.Services;

public static class ThemeService
{
    public static void Apply(ThemeMode themeMode)
    {
        var palette = ResolvePalette(themeMode);
        UpdateBrush("AppBackgroundBrush", palette.AppBackground);
        UpdateBrush("PanelBrush", palette.Panel);
        UpdateBrush("CardBrush", palette.Card);
        UpdateBrush("CardBorderBrush", palette.CardBorder);
        UpdateBrush("SoftTextBrush", palette.SoftText);
        UpdateBrush("BrightTextBrush", palette.BrightText);
        UpdateBrush("MutedTextBrush", palette.MutedText);
        UpdateBrush("ShellBorderBrush", palette.ShellBorder);
        UpdateBrush("NavHoverBrush", palette.NavHover);
        UpdateBrush("NavActiveBrush", palette.NavActive);
        UpdateBrush("ModeBadgeBrush", palette.ModeBadge);
        UpdateBrush("ModeBadgeBorderBrush", palette.ModeBadgeBorder);
        UpdateBrush("ModeBadgeTextBrush", palette.ModeBadgeText);
        UpdateBrush("SummaryBoxBrush", palette.SummaryBox);
    }

    private static ThemePalette ResolvePalette(ThemeMode themeMode)
    {
        var effectiveMode = themeMode == ThemeMode.System ? DetectSystemTheme() : themeMode;
        return effectiveMode == ThemeMode.Light
            ? new ThemePalette(
                AppBackground: MediaColor.FromRgb(243, 246, 252),
                Panel: MediaColor.FromRgb(232, 238, 248),
                Card: MediaColor.FromRgb(255, 255, 255),
                CardBorder: MediaColor.FromRgb(208, 219, 235),
                SoftText: MediaColor.FromRgb(89, 102, 124),
                BrightText: MediaColor.FromRgb(17, 24, 39),
                MutedText: MediaColor.FromRgb(102, 112, 133),
                ShellBorder: MediaColor.FromRgb(198, 211, 229),
                NavHover: MediaColor.FromRgb(229, 238, 251),
                NavActive: MediaColor.FromRgb(31, 52, 89),
                ModeBadge: MediaColor.FromRgb(235, 243, 255),
                ModeBadgeBorder: MediaColor.FromRgb(193, 211, 240),
                ModeBadgeText: MediaColor.FromRgb(27, 110, 243),
                SummaryBox: MediaColor.FromRgb(247, 250, 255))
            : new ThemePalette(
                AppBackground: MediaColor.FromRgb(8, 17, 31),
                Panel: MediaColor.FromRgb(13, 26, 47),
                Card: MediaColor.FromRgb(17, 31, 54),
                CardBorder: MediaColor.FromRgb(36, 52, 79),
                SoftText: MediaColor.FromRgb(168, 179, 199),
                BrightText: MediaColor.FromRgb(245, 248, 255),
                MutedText: MediaColor.FromRgb(102, 112, 133),
                ShellBorder: MediaColor.FromRgb(28, 42, 66),
                NavHover: MediaColor.FromRgb(24, 40, 66),
                NavActive: MediaColor.FromRgb(36, 60, 99),
                ModeBadge: MediaColor.FromRgb(23, 43, 72),
                ModeBadgeBorder: MediaColor.FromRgb(44, 74, 118),
                ModeBadgeText: MediaColor.FromRgb(88, 166, 255),
                SummaryBox: MediaColor.FromRgb(11, 21, 38));
    }

    private static ThemeMode DetectSystemTheme()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return value is int intValue && intValue > 0 ? ThemeMode.Light : ThemeMode.Dark;
        }
        catch
        {
            return ThemeMode.Dark;
        }
    }

    private static void UpdateBrush(string key, MediaColor color)
    {
        System.Windows.Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private sealed record ThemePalette(
        MediaColor AppBackground,
        MediaColor Panel,
        MediaColor Card,
        MediaColor CardBorder,
        MediaColor SoftText,
        MediaColor BrightText,
        MediaColor MutedText,
        MediaColor ShellBorder,
        MediaColor NavHover,
        MediaColor NavActive,
        MediaColor ModeBadge,
        MediaColor ModeBadgeBorder,
        MediaColor ModeBadgeText,
        MediaColor SummaryBox);
}
