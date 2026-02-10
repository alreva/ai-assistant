using Azure;
using Azure.Data.Tables;

namespace DevicePairingFunction.Models;

public class DeviceEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "device";
    public string RowKey { get; set; } = string.Empty; // Device ID
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string DeviceSecretHash { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string? EncryptedRefreshToken { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LinkedAt { get; set; }
}
