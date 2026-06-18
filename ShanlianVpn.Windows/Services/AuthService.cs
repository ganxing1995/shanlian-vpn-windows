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

    public async Task RegisterAsync(string email, string password)
    {
        try
        {
            await _api.PostAsync("/api/auth/register", new { email, password }, includeAuth: false);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            throw new ApiException("注册功能暂未开放，请联系客服。", ex.StatusCode, "register_unavailable");
        }
    }

    public async Task ChangePasswordAsync(string currentPassword, string newPassword, string confirmPassword)
    {
        if (newPassword != confirmPassword)
        {
            throw new ApiException("两次输入的新密码不一致", errorCode: "password_confirm_mismatch");
        }

        if (newPassword.Length < 8)
        {
            throw new ApiException("新密码至少 8 位", errorCode: "password_too_short");
        }

        try
        {
            await _api.PostAsync("/api/account/change-password", new
            {
                current_password = currentPassword,
                new_password = newPassword,
                new_password_confirmation = confirmPassword
            });
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            TokenStore.Clear();
            throw new ApiException("登录已过期，请重新登录", ex.StatusCode, ex.ErrorCode);
        }
        catch (ApiException ex) when (ex.StatusCode is 403 or 422)
        {
            throw new ApiException("当前密码不正确", ex.StatusCode, ex.ErrorCode);
        }
    }

    public async Task<User> GetUserAsync()
    {
        try
        {
            var response = await _api.GetAsync("/api/auth/me");
            return ParseUser(ExtractUserElement(response));
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            try
            {
                var response = await _api.GetAsync("/api/user");
                return ParseUser(ExtractUserElement(response));
            }
            catch (ApiException userEx) when (userEx.StatusCode == 404)
            {
                var response = await _api.GetAsync("/api/me");
                return ParseUser(ExtractUserElement(response));
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

    private static JsonElement ExtractUserElement(JsonElement element) =>
        JsonHelpers.TryGetProperty(element, "user", out var userElement) ? userElement : element;
}
