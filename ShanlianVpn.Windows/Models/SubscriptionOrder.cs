namespace ShanlianVpn.Windows.Models;

public sealed class SubscriptionOrder
{
    public string OrderNo { get; set; } = "";
    public string PlanName { get; set; } = "";
    public decimal Amount { get; set; }
    public string Status { get; set; } = "pending";
    public string PaymentUrl { get; set; } = "";
}

