namespace ShanlianVpn.Windows.Models;

public sealed class Plan
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }

    public string DisplayName => Name.ToLowerInvariant() switch
    {
        var name when name.Contains("weekly") || name.Contains("week") => "周套餐",
        var name when name.Contains("monthly") || name.Contains("month") => "月套餐",
        var name when name.Contains("yearly") || name.Contains("year") => "年度套餐",
        _ => string.IsNullOrWhiteSpace(Name) ? "套餐" : Name
    };
}

