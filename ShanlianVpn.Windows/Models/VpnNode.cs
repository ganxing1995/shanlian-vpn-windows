using System.Text.Json.Serialization;

namespace ShanlianVpn.Windows.Models;

public sealed class VpnNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("country")]
    public string Country { get; set; } = "";

    [JsonPropertyName("country_code")]
    public string CountryCode { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    public string DisplayCountry
    {
        get
        {
            var country = Country;
            return CountryCode.ToUpperInvariant() switch
            {
                "US" or "USA" => "🇺🇸 美国",
                "JP" or "JPN" => "🇯🇵 日本",
                _ when country.Contains("美国", StringComparison.OrdinalIgnoreCase) || country.Contains("United States", StringComparison.OrdinalIgnoreCase) => "🇺🇸 美国",
                _ when country.Contains("日本", StringComparison.OrdinalIgnoreCase) || country.Contains("Japan", StringComparison.OrdinalIgnoreCase) => "🇯🇵 日本",
                _ => string.IsNullOrWhiteSpace(country) ? "未知线路" : country
            };
        }
    }
}
