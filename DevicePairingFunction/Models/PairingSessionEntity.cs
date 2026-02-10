using Azure;
using Azure.Data.Tables;

namespace DevicePairingFunction.Models;

public class PairingSessionEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "session";
    public string RowKey { get; set; } = string.Empty; // Session ID
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string DeviceId { get; set; } = string.Empty;
    public string CodeVerifier { get; set; } = string.Empty; // PKCE code verifier
    public string Status { get; set; } = "pending"; // pending, authenticated, approved, expired
    public DateTime ExpiresAt { get; set; }
    public string? UserId { get; set; }
    public string? EncryptedTokens { get; set; } // Encrypted tokens after OAuth callback

    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
}
