using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Configuration;

namespace CallAutomation.AzureAI.VoiceLive
{
    /// <summary>
    /// Minimal Azure Speech STT helper that creates a PushAudioInputStream + SpeechRecognizer
    /// targeting raw PCM 16kHz, 16-bit, mono (aligned with ACS media streaming).
    /// </summary>
    public sealed class SpeechSttService
    {
        private readonly SpeechConfig _cfg;
        private readonly string _locale;

        public SpeechSttService(IConfiguration config)
        {
            var key = config["SpeechKey"] ?? throw new ArgumentNullException("SpeechKey");
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

            // Recognition language (falls back to VOICE_LOCALE or en-US)
            _locale = config["VOICE_LOCALE"] ?? "en-US";
            _cfg.SpeechRecognitionLanguage = _locale;
            // If you later need partial results punctuation/timestamps, set properties here.
        }

        public sealed class SttSession : IAsyncDisposable
        {
            private readonly PushAudioInputStream _push;
            private readonly AudioConfig _audioCfg;
            private readonly SpeechRecognizer _reco;

            public SpeechRecognizer Recognizer => _reco;

            internal SttSession(PushAudioInputStream push, AudioConfig audioCfg, SpeechRecognizer reco)
            {
                _push = push;
                _audioCfg = audioCfg;
                _reco = reco;
            }

            public void PushPcm(byte[] pcm) => _push.Write(pcm);

            public async Task StartAsync() => await _reco.StartContinuousRecognitionAsync().ConfigureAwait(false);

            public async Task StopAsync()
            {
                try { await _reco.StopContinuousRecognitionAsync().ConfigureAwait(false); } catch { /* no-op */ }
                try { _push.Close(); } catch { /* no-op */ }
            }

            public async ValueTask DisposeAsync()
            {
                await StopAsync();
                _reco.Dispose();
                _audioCfg.Dispose();
                _push.Dispose();
            }
        }

        /// <summary>
        /// Create a recognizer session wired for 16kHz/16-bit/mono PCM push input.
        /// Caller must attach event handlers on the returned SpeechRecognizer before calling StartAsync().
        /// </summary>
        public SttSession CreatePcm16kMonoSession()
        {
            // Set precise raw PCM format: 16kHz, 16-bit, mono
            var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
            var push = AudioInputStream.CreatePushStream(format);
            var audioCfg = AudioConfig.FromStreamInput(push);

            var reco = new SpeechRecognizer(_cfg, audioCfg);
            return new SttSession(push, audioCfg, reco);
        }
    }
}