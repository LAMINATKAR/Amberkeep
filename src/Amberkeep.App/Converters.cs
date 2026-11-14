using Amberkeep.App.ViewModels;
using Amberkeep.Core.Diff;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Amberkeep.App;

/// <summary>Converters used by the views. Colors are mid-tones that work in light and dark mode.</summary>
public static class Converters
{
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#3FA45B"));
    public static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#3B82C4"));
    public static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#D4911F"));
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#D2453F"));
    public static readonly IBrush Violet = new SolidColorBrush(Color.Parse("#8E6CC9"));
    public static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#8A8580"));

    public static readonly IValueConverter CategoryBrush = new FuncValueConverter<DiffCategory?, IBrush>(c => c switch
    {
        DiffCategory.New => Green,
        DiffCategory.Modified => Blue,
        DiffCategory.TargetNewer => Amber,
        DiffCategory.Removed => Red,
        DiffCategory.CaseRename => Violet,
        DiffCategory.Error => Red,
        _ => Gray,
    });

    public static readonly IValueConverter TargetStateBrush = new FuncValueConverter<TargetDisplayState, IBrush>(s => s switch
    {
        TargetDisplayState.Connected => Green,
        TargetDisplayState.Unverified => Amber,
        _ => Gray,
    });

    public static readonly IValueConverter BannerBrush = new FuncValueConverter<BannerKind, IBrush>(k => k switch
    {
        BannerKind.Error => Red,
        BannerKind.Warning => Amber,
        _ => Gray,
    });

    public static readonly IValueConverter OutcomeBrush = new FuncValueConverter<BannerKind, IBrush>(k => k switch
    {
        BannerKind.Error => Red,
        BannerKind.Warning => Amber,
        _ => Green,
    });

    public static readonly IValueConverter CategoryText = new FuncValueConverter<DiffCategory, string>(Services.Format.Category);

    public static readonly IValueConverter Indent = new FuncValueConverter<double, Thickness>(d => new Thickness(d, 0, 0, 0));

    /// <summary>Chevron for an expandable folder row.</summary>
    public static readonly IValueConverter Chevron = new FuncValueConverter<bool, string>(expanded => expanded ? "▾" : "▸");
}
