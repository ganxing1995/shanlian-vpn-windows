namespace ShanlianVpn.Windows.Models;

public sealed class Plan
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }

    public string DisplayName => Subscription.NormalizePlanName(Name) is { Length: > 0 } normalized ? normalized : "套餐";

    public decimal DisplayUsdAmount => DisplayName switch
    {
        "周套餐" => 2.50m,
        "月套餐" => 6.00m,
        "年度套餐" => 50.00m,
        _ => Amount
    };

    public string BillingCycleDisplay => DisplayName switch
    {
        "周套餐" => "/ 周",
        "月套餐" => "/ 月",
        "年度套餐" => "/ 年",
        _ => ""
    };

    public string PriceDisplay => $"${DisplayUsdAmount:0.00} {BillingCycleDisplay}".TrimEnd();
}
