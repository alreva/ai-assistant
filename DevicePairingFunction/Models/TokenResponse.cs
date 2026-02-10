using System.Text.Json.Serialization;

namespace DevicePairingFunction.Models;

public class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";
}

public class DeviceTokenRequest
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceSecret")]
    public string DeviceSecret { get; set; } = string.Empty;
}

public class DeviceRegisterRequest
{
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("deviceSecret")]
    public string DeviceSecret { get; set; } = string.Empty;
}

public class DeviceTokenResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("expiresIn")]
    public int ExpiresIn { get; set; }
}

public class PairingStatusResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}
