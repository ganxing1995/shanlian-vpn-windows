using System.Text.Json;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class PlanService
{
    private readonly ApiClient _api = new();

    public async Task<IReadOnlyList<Plan>> GetPlansAsync()
    {
        var response = await _api.GetAsync("/api/plans");
        var array = response.ValueKind == JsonValueKind.Array
            ? response
            : JsonHelpers.TryGetProperty(response, "plans", out var plans) ? plans : default;

        if (array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<Plan>();
        foreach (var item in array.EnumerateArray())
        {
            result.Add(new Plan
            {
                Id = JsonHelpers.GetString(item, "id", "plan_id"),
                Name = JsonHelpers.GetString(item, "name", "title", "plan_name", "type"),
                Amount = JsonHelpers.GetDecimal(item, "amount", "price")
            });
        }

        return result;
    }

    public async Task<SubscriptionOrder> CreateOrderAsync(string planId, string type)
    {
        var response = await _api.PostAsync("/api/subscription-orders", new
        {
            plan_id = planId,
            type
        });

        return new SubscriptionOrder
        {
            OrderNo = JsonHelpers.GetString(response, "order_no", "order_number", "no", "id"),
            PlanName = JsonHelpers.GetString(response, "plan_name", "plan", "plan_title"),
            Amount = JsonHelpers.GetDecimal(response, "amount", "price"),
            Status = JsonHelpers.GetString(response, "status") is { Length: > 0 } status ? status : "pending",
            PaymentUrl = JsonHelpers.GetString(response, "payment_url", "pay_url")
        };
    }
}

