using System.Text.Json;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class AuthService
{
    private readonly ApiClient _api = new();

    public async Task<User> LoginAsync(string email, string password)
    {
        var body = new { email, password };
        JsonElement response;

        try
        {
            response = await _api.PostAsync("/api/auth/login", body, includeAuth: false);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            response = await _api.PostAsync("/api/login", body, includeAuth: false);
        }
        catch (ApiException ex) when (ex.StatusCode is 401 or 422)
        {
            throw new ApiException("邮箱或密码错误", ex.StatusCode, ex.ErrorCode);
        }

        var token = JsonHelpers.GetString(response, "token", "access_token", "plainTextToken");
        if (string.IsNullOrWhiteSpace(token) && JsonHelpers.TryGetProperty(response, "authorization", out var auth))
        {
            token = JsonHelpers.GetString(auth, "token", "access_token");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ApiException("服务器错误，请稍后重试", errorCode: "missing_token");
        }

        TokenStore.SaveToken(token);
        SafeLogger.Info("login_success");

        if (JsonHelpers.TryGetProperty(response, "user", out var userElement))
        {
            return ParseUser(userElement, email);
        }

        return await GetUserAsync();
    }

    public async Task<User> GetUserAsync()
    {
        try
        {
            var response = await _api.GetAsync("/api/auth/me");
            return ParseUser(response);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            try
            {
                var response = await _api.GetAsync("/api/user");
                return ParseUser(response);
            }
            catch (ApiException userEx) when (userEx.StatusCode == 404)
            {
                var response = await _api.GetAsync("/api/me");
                return ParseUser(response);
            }
        }
    }

    public void Logout() => TokenStore.Clear();

    private static User ParseUser(JsonElement element, string fallbackEmail = "") =>
        new()
        {
            Id = JsonHelpers.GetString(element, "id", "user_id"),
            Name = JsonHelpers.GetString(element, "name", "nickname"),
            Email = JsonHelpers.GetString(element, "email") is { Length: > 0 } email ? email : fallbackEmail
        };
}
