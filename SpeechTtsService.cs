using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Configuration;

namespace CallAutomation.AzureAI.VoiceLive
{
    /// <summary>
    /// Azure Speech TTS wrapper that returns raw 16 kHz, 16-bit, mono PCM frames
    /// sized for ACS (~20 ms -> 640 bytes at 16 kHz / 16-bit / mono).
    /// </summary>
    public sealed class SpeechTtsService
    {
        private readonly SpeechConfig _cfg;
        private readonly string _voice;

        public SpeechTtsService(IConfiguration config)
        {
            var key = config["SpeechKey"] ?? throw new ArgumentNullException("SpeechKey");

            // Prefer endpoint for multi-service Cognitive resources; fallback to region for Speech resources.
            var endpoint = config["CognitiveServiceEndpoint"];
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                _cfg = SpeechConfig.FromEndpoint(new Uri(endpoint!), key);
            }
            else
            {
                var region = config["SpeechRegion"] ?? throw new ArgumentNullException("SpeechRegion");
                _cfg = SpeechConfig.FromSubscription(key, region);
            }

            _cfg.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm);

            _voice = config["DefaultVoice"] ?? "en-US-AriaNeural";
            _cfg.SpeechSynthesisVoiceName = _voice;
        }

        /// <summary>
        /// Synthesize text to raw PCM and yield ACS-friendly frames.
        /// </summary>
        public async IAsyncEnumerable<byte[]> SpeakTextFramesAsync(
            string text,
            int frameSizeBytes = 640,
            int paceDelayMsPerFrame = 20,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Console.WriteLine($"[SpeechTTS] Synthesizing text: '{text}'");
            if (string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine("[SpeechTTS] Text is null or whitespace. Skipping synthesis.");
                yield break;
            }

            byte[] pcm = Array.Empty<byte>();

            // Synthesize to an in-memory pull stream (headless-friendly).
            try
            {
                using var outStream = AudioOutputStream.CreatePullStream();
                using var audioCfg  = AudioConfig.FromStreamOutput(outStream);
                using var synth     = new SpeechSynthesizer(_cfg, audioCfg);

                var result = await synth.SpeakTextAsync(text).ConfigureAwait(false);
                Console.WriteLine($"[SpeechTTS] Synthesis result: {result.Reason}");

                if (result.Reason != ResultReason.SynthesizingAudioCompleted)
                {
                    var err = SpeechSynthesisCancellationDetails.FromResult(result);
                    Console.WriteLine($"[SpeechTTS] TTS failed: {result.Reason} ({err?.Reason}; {err?.ErrorDetails})");
                    yield break;
                }

                // Prefer direct bytes if provided (already PCM per output format).
                pcm = result.AudioData ?? Array.Empty<byte>();

                // If empty, read from the pull stream.
                if (pcm.Length == 0)
                {
                    using var ms = new MemoryStream();
                    var buf = new byte[4096];
                    uint readU;
                    while ((readU = outStream.Read(buf)) > 0)
                    {
                        ms.Write(buf, 0, (int)readU); // cast is safe for <= buffer length
                    }
                    pcm = ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SpeechTTS] Exception: {ex.GetType().Name}: {ex.Message}");
                yield break;
            }

            Console.WriteLine($"[SpeechTTS] Synthesized PCM length: {pcm.Length} bytes.");

            // Yield ~20 ms frames (640 bytes at 16 kHz / 16-bit / mono)
            for (int i = 0; i < pcm.Length; i += frameSizeBytes)
            {
                var len = Math.Min(frameSizeBytes, pcm.Length - i);
                var chunk = new byte[len];
                Buffer.BlockCopy(pcm, i, chunk, 0, len);

                Console.WriteLine($"[SpeechTTS] Yielding PCM chunk of {len} bytes.");
                yield return chunk;

                if (ct.IsCancellationRequested) yield break;
                if (paceDelayMsPerFrame > 0) await Task.Delay(paceDelayMsPerFrame, ct);
            }
        }
    }
}