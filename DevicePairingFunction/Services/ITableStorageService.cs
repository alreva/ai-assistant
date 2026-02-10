using DevicePairingFunction.Models;

namespace DevicePairingFunction.Services;

public interface ITableStorageService
{
    Task<PairingSessionEntity?> GetPairingSessionAsync(string sessionId);
    Task CreatePairingSessionAsync(PairingSessionEntity session);
    Task UpdatePairingSessionAsync(PairingSessionEntity session);
    Task DeletePairingSessionAsync(string sessionId);

    Task<DeviceEntity?> GetDeviceAsync(string deviceId);
    Task CreateDeviceAsync(DeviceEntity device);
    Task UpdateDeviceAsync(DeviceEntity device);
    Task<bool> DeviceExistsAsync(string deviceId);
}
