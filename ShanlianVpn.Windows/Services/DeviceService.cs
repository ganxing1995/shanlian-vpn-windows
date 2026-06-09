using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class DeviceService
{
    private readonly ApiClient _api = new();

    public async Task<DeviceRegistrationResult> RegisterWindowsDeviceAsync(string deviceId)
    {
        var body = new
        {
            device_id = deviceId,
            deviceName = "Windows PC",
            platform = "Windows",
            app_version = "1.0.0",
            model = $"Windows {Environment.OSVersion.VersionString}"
        };

        try
        {
            var response = await _api.PostAsync("/api/devices/register", body);
            SafeLogger.Info("device_registered");
            return new DeviceRegistrationResult
            {
                IsAllowed = JsonHelpers.GetBool(response, true, "allowed", "is_allowed", "can_register", "can_use"),
                Message = JsonHelpers.GetString(response, "message")
            };
        }
        catch (ApiException ex) when (ex.StatusCode is 403 or 409)
        {
            return new DeviceRegistrationResult
            {
                IsAllowed = false,
                Message = "设备数量已达上限，请先在手机端或后台移除旧设备。"
            };
        }
    }

    public async Task<IReadOnlyList<Device>> GetDevicesAsync(string deviceId)
    {
        var response = await _api.GetWithDeviceIdAsync("/api/devices", deviceId);
        var listRoot = response;
        if (response.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            return ParseDevices(listRoot);
        }

        if (JsonHelpers.TryGetProperty(response, "devices", out var devices))
        {
            return ParseDevices(devices);
        }

        return [];
    }

    public async Task RemoveDeviceAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ApiException("设备信息异常，请稍后重试", errorCode: "device_id_missing");
        }

        await _api.DeleteAsync($"/api/devices/{Uri.EscapeDataString(id)}");
    }

    private static IReadOnlyList<Device> ParseDevices(System.Text.Json.JsonElement devices)
    {
        if (devices.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<Device>();
        foreach (var item in devices.EnumerateArray())
        {
            result.Add(new Device
            {
                Id = JsonHelpers.GetString(item, "id"),
                DeviceId = JsonHelpers.GetString(item, "device_id"),
                DeviceName = JsonHelpers.GetString(item, "deviceName", "device_name", "name"),
                Platform = JsonHelpers.GetString(item, "platform"),
                Model = JsonHelpers.GetString(item, "model")
            });
        }

        return result;
    }
}
