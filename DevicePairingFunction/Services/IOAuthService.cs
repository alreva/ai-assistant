using DevicePairingFunction.Models;

namespace DevicePairingFunction.Services;

public interface IOAuthService
{
    string GenerateCodeVerifier();
    string GenerateCodeChallenge(string codeVerifier);
    string BuildAuthorizationUrl(string sessionId, string codeChallenge);
    Task<TokenResponse> ExchangeCodeForTokensAsync(string code, string codeVerifier);
    Task<TokenResponse> RefreshTokenAsync(string refreshToken);
    string? GetUserIdFromToken(string accessToken);
}
