using System.Security;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging;
using SpeechService.Models;

namespace SpeechService.Services;

public class TtsService : ITtsService
{
    private readonly AzureSpeechConfig _config;
    private readonly ILogger<TtsService> _logger;

    public TtsService(AzureSpeechConfig config, ILogger<TtsService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SynthesizeToStreamAsync(TtsRequest request, Func<byte[], Task> onAudioChunk, CancellationToken ct)
    {
        var speechConfig = SpeechConfig.FromSubscription(_config.ApiKey, _config.Region);
        speechConfig.SetSpeechSynthesisOutputFormat(ParseOutputFormat(request.OutputFormat));

        using var synthesizer = new SpeechSynthesizer(speechConfig, null);

        // Use raw SSML if provided, otherwise build from parameters
        var ssml = !string.IsNullOrEmpty(request.Ssml) ? request.Ssml : BuildSsml(request);
        _logger.LogInformation("Calling Azure Speech API (streaming), using {SsmlSource}...",
            !string.IsNullOrEmpty(request.Ssml) ? "provided SSML" : "built SSML");

        // Use a channel to properly handle async streaming
        var channel = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        var totalBytes = 0;

        synthesizer.Synthesizing += (s, e) =>
        {
            if (e.Result.AudioData.Length > 0)
            {
                totalBytes += e.Result.AudioData.Length;
                // Write to channel synchronously (non-blocking for unbounded)
                channel.Writer.TryWrite(e.Result.AudioData.ToArray());
            }
        };

        // Start synthesis (don't await yet)
        var synthesisTask = synthesizer.SpeakSsmlAsync(ssml);

        // Stream chunks as they arrive
        var chunkCount = 0;
        _ = Task.Run(async () =>
        {
            await synthesisTask;
            channel.Writer.Complete();
        }, ct);

        await foreach (var chunk in channel.Reader.ReadAllAsync(ct))
        {
            chunkCount++;
            await onAudioChunk(chunk);
        }

        // Check result
        var result = await synthesisTask;
        if (result.Reason == ResultReason.Canceled)
        {
            var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
            _logger.LogError("Speech synthesis canceled: {Reason}, {Details}", cancellation.Reason, cancellation.ErrorDetails);
            throw new InvalidOperationException($"Speech synthesis failed: {cancellation.ErrorDetails}");
        }

        _logger.LogInformation("Streamed {Bytes} bytes in {Chunks} chunks", totalBytes, chunkCount);
    }

    private static string BuildSsml(TtsRequest request)
    {
        var escapedText = SecurityElement.Escape(request.Text) ?? string.Empty;
        var voiceName = SecurityElement.Escape(request.Voice) ?? "en-US-JennyNeural";
        var rate = SecurityElement.Escape(request.Rate) ?? "0%";
        var pitch = SecurityElement.Escape(request.Pitch) ?? "0%";

        var langCode = voiceName.Length >= 5 ? voiceName[..5] : "en-US";

        var innerContent = $"<prosody rate=\"{rate}\" pitch=\"{pitch}\">{escapedText}</prosody>";

        if (!string.IsNullOrEmpty(request.Style))
        {
            var style = SecurityElement.Escape(request.Style);
            innerContent = $"<mstts:express-as style=\"{style}\">{innerContent}</mstts:express-as>";
        }

        return $"""
            <speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xmlns:mstts="https://www.w3.org/2001/mstts" xml:lang="{langCode}">
              <voice name="{voiceName}">
                {innerContent}
              </voice>
            </speak>
            """;
    }

    private static SpeechSynthesisOutputFormat ParseOutputFormat(string format) => format switch
    {
        "audio-16khz-32kbitrate-mono-mp3" => SpeechSynthesisOutputFormat.Audio16Khz32KBitRateMonoMp3,
        "audio-16khz-64kbitrate-mono-mp3" => SpeechSynthesisOutputFormat.Audio16Khz64KBitRateMonoMp3,
        "audio-16khz-128kbitrate-mono-mp3" => SpeechSynthesisOutputFormat.Audio16Khz128KBitRateMonoMp3,
        "audio-24khz-48kbitrate-mono-mp3" => SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3,
        "audio-24khz-96kbitrate-mono-mp3" => SpeechSynthesisOutputFormat.Audio24Khz96KBitRateMonoMp3,
        "audio-24khz-160kbitrate-mono-mp3" => SpeechSynthesisOutputFormat.Audio24Khz160KBitRateMonoMp3,
        "audio-48khz-96kbitrate-mono-mp3" => SpeechSynthesisOutputFormat.Audio48Khz96KBitRateMonoMp3,
        "audio-48khz-192kbitrate-mono-mp3" => SpeechSynthesisOutputFormat.Audio48Khz192KBitRateMonoMp3,
        "riff-16khz-16bit-mono-pcm" => SpeechSynthesisOutputFormat.Riff16Khz16BitMonoPcm,
        "riff-24khz-16bit-mono-pcm" => SpeechSynthesisOutputFormat.Riff24Khz16BitMonoPcm,
        "riff-48khz-16bit-mono-pcm" => SpeechSynthesisOutputFormat.Riff48Khz16BitMonoPcm,
        "raw-16khz-16bit-mono-pcm" => SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm,
        "raw-24khz-16bit-mono-pcm" => SpeechSynthesisOutputFormat.Raw24Khz16BitMonoPcm,
        "raw-48khz-16bit-mono-pcm" => SpeechSynthesisOutputFormat.Raw48Khz16BitMonoPcm,
        "ogg-16khz-16bit-mono-opus" => SpeechSynthesisOutputFormat.Ogg16Khz16BitMonoOpus,
        "ogg-24khz-16bit-mono-opus" => SpeechSynthesisOutputFormat.Ogg24Khz16BitMonoOpus,
        "ogg-48khz-16bit-mono-opus" => SpeechSynthesisOutputFormat.Ogg48Khz16BitMonoOpus,
        "webm-16khz-16bit-mono-opus" => SpeechSynthesisOutputFormat.Webm16Khz16BitMonoOpus,
        "webm-24khz-16bit-mono-opus" => SpeechSynthesisOutputFormat.Webm24Khz16BitMonoOpus,
        _ => SpeechSynthesisOutputFormat.Audio16Khz32KBitRateMonoMp3
    };
}
