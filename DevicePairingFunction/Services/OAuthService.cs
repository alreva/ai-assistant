using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevicePairingFunction.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevicePairingFunction.Services;

public class OAuthService : IOAuthService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OAuthService> _logger;
    private readonly string _tenantId;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _scope;
    private readonly string _baseUrl;

    public OAuthService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<OAuthService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _logger = logger;
        _tenantId = configuration["AzureAd:TenantId"]
            ?? throw new InvalidOperationException("AzureAd:TenantId not configured");
        _clientId = configuration["AzureAd:ClientId"]
            ?? throw new InvalidOperationException("AzureAd:ClientId not configured");
        _clientSecret = configuration["AzureAd:ClientSecret"]
            ?? throw new InvalidOperationException("AzureAd:ClientSecret not configured");
        _scope = configuration["AzureAd:Scope"]
            ?? throw new InvalidOperationException("AzureAd:Scope not configured");
        _baseUrl = configuration["BaseUrl"]
            ?? throw new InvalidOperationException("BaseUrl not configured");
    }

    public string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    public string GenerateCodeChallenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(bytes);
    }

    public string BuildAuthorizationUrl(string sessionId, string codeChallenge)
    {
        var redirectUri = $"{_baseUrl}/api/callback";
        var state = Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sessionId })));

        var authUrl = $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/authorize" +
            $"?client_id={Uri.EscapeDataString(_clientId)}" +
            $"&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(_scope)}" +
            $"&state={state}" +
            $"&code_challenge={codeChallenge}" +
            $"&code_challenge_method=S256";

        _logger.LogInformation("Built authorization URL for session {SessionId}", sessionId);
        return authUrl;
    }

    public async Task<TokenResponse> ExchangeCodeForTokensAsync(string code, string codeVerifier)
    {
        var redirectUri = $"{_baseUrl}/api/callback";
        var tokenEndpoint = $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/token";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = _scope
        });

        var response = await _httpClient.PostAsync(tokenEndpoint, content);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Token exchange failed: {StatusCode} - {Response}",
                response.StatusCode, responseContent);
            throw new InvalidOperationException($"Token exchange failed: {responseContent}");
        }

        var tokens = JsonSerializer.Deserialize<TokenResponse>(responseContent)
            ?? throw new InvalidOperationException("Failed to parse token response");

        _logger.LogInformation("Successfully exchanged code for tokens");
        return tokens;
    }

    public async Task<TokenResponse> RefreshTokenAsync(string refreshToken)
    {
        var tokenEndpoint = $"https://login.microsoftonline.com/{_tenantId}/oauth2/v2.0/token";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = _scope
        });

        var response = await _httpClient.PostAsync(tokenEndpoint, content);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Token refresh failed: {StatusCode} - {Response}",
                response.StatusCode, responseContent);
            throw new InvalidOperationException($"Token refresh failed: {responseContent}");
        }

        var tokens = JsonSerializer.Deserialize<TokenResponse>(responseContent)
            ?? throw new InvalidOperationException("Failed to parse token response");

        _logger.LogInformation("Successfully refreshed tokens");
        return tokens;
    }

    public string? GetUserIdFromToken(string accessToken)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var token = handler.ReadJwtToken(accessToken);
            return token.Claims.FirstOrDefault(c => c.Type == "oid")?.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract user ID from token");
            return null;
        }
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
