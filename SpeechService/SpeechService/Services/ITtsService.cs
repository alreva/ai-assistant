using SpeechService.Models;

namespace SpeechService.Services;

public interface ITtsService
{
    Task SynthesizeToStreamAsync(TtsRequest request, Func<byte[], Task> onAudioChunk, CancellationToken ct);
}
