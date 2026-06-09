using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class ApiClient
{
    private static readonly HttpClient HttpClient = new()
    {
        BaseAddress = new Uri("https://api.lianshu.shop"),
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<JsonElement> GetAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        AddAuth(request);
        return await SendAsync(request, cancellationToken);
    }

    public async Task<JsonElement> GetWithDeviceIdAsync(string endpoint, string deviceId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        AddAuth(request);
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            request.Headers.TryAddWithoutValidation("X-Device-Id", deviceId);
        }

        return await SendAsync(request, cancellationToken);
    }

    public async Task<JsonElement> PostAsync(string endpoint, object body, bool includeAuth = true, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };

        if (includeAuth)
        {
            AddAuth(request);
        }

        return await SendAsync(request, cancellationToken);
    }

    public static T? Deserialize<T>(JsonElement element) =>
        JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions);

    private static void AddAuth(HttpRequestMessage request)
    {
        var token = TokenStore.ReadToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    private static async Task<JsonElement> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await HttpClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SafeLogger.Error("network_timeout");
            throw new ApiException("网络错误，请稍后重试", errorCode: "network_timeout");
        }
        catch (HttpRequestException)
        {
            SafeLogger.Error("network_error");
            throw new ApiException("网络错误，请稍后重试", errorCode: "network_error");
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = TryParseJson(responseText);
        var root = document?.RootElement.Clone() ?? EmptyJsonObject();

        if (response.IsSuccessStatusCode)
        {
            return JsonHelpers.UnwrapData(root).Clone();
        }

        var statusCode = (int)response.StatusCode;
        var apiMessage = JsonHelpers.GetString(root, "message", "error");
        var apiCode = JsonHelpers.GetString(root, "code", "error_code");
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "登录已过期，请重新登录",
            HttpStatusCode.Forbidden when apiCode.Contains("device", StringComparison.OrdinalIgnoreCase) => "设备数量已达上限，请先在手机端或后台移除旧设备。",
            HttpStatusCode.Forbidden => string.IsNullOrWhiteSpace(apiMessage) ? "服务器错误，请稍后重试" : apiMessage,
            HttpStatusCode.UnprocessableEntity => string.IsNullOrWhiteSpace(apiMessage) ? "邮箱或密码错误" : apiMessage,
            _ => string.IsNullOrWhiteSpace(apiMessage) ? "服务器错误，请稍后重试" : apiMessage
        };

        SafeLogger.Error(string.IsNullOrWhiteSpace(apiCode) ? $"http_{statusCode}" : apiCode);
        throw new ApiException(message, statusCode, apiCode);
    }

    private static JsonDocument? TryParseJson(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(responseText);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement EmptyJsonObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
