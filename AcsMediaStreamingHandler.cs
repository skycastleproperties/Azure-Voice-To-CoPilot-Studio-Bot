// AcsMediaStreamingHandler.cs
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Azure.Communication.CallAutomation;
using Microsoft.Extensions.Configuration;

namespace CallAutomation.AzureAI.VoiceLive
{
    /// <summary>
    /// ACS media streaming handler:
    /// - Receives AudioMetadata + AudioData over WebSocket.
    /// - Streams bot replies (TTS) back as outbound audioData frames.
    /// - STT: Feeds inbound PCM to Azure Speech (push stream + recognizer).
    /// - On final recognition -> sends text to Copilot (Direct Line) and speaks reply.
    /// - Barge-in: on speech start, send stopAudio + cancel any ongoing TTS.
    /// </summary>
    public sealed class AcsMediaStreamingHandler
    {
        private readonly System.Net.WebSockets.WebSocket _ws;
        private readonly IConfiguration _config;
        private readonly SpeechTtsService _tts;
        private readonly SpeechSttService _stt;
        private readonly string _corrId;
        private readonly string? _callerId;

        private CallConnection? _call;

        // Audio shape (confirmed by AudioMetadata)
        private int _sampleRate = 16000;
        private int _channels = 1;

        // Gate until we see AudioMetadata
        private readonly TaskCompletionSource<bool> _metadataReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        // STT session
        private SpeechSttService.SttSession? _sttSession;

        // Direct Line per call
        private CopilotDirectLineService? _dl;

        // Speaking state + cancellation (for barge-in)
        private volatile bool _isSpeaking;
        private readonly object _speakLock = new();
        private CancellationTokenSource? _speakCts;
        private volatile bool _stopSent; // ensures one stopAudio per episode

        // Simple queueing of bot replies (so we speak them serially)
        private readonly BlockingCollection<string> _botReplies = new();

        public AcsMediaStreamingHandler(
            System.Net.WebSockets.WebSocket webSocket,
            IConfiguration config,
            SpeechTtsService tts,
            SpeechSttService stt,
            string corrId,
            string? callerId)
        {
            _ws = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _tts = tts ?? throw new ArgumentNullException(nameof(tts));
            _stt = stt ?? throw new ArgumentNullException(nameof(stt));
            _corrId = corrId ?? throw new ArgumentNullException(nameof(corrId));
            _callerId = string.IsNullOrWhiteSpace(callerId) ? null : callerId;
        }

        public void SetCallConnection(CallConnection call) => _call = call;

        public async Task ProcessWebSocketAsync(CancellationToken ct = default)
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var lct = linkedCts.Token;

            // 1) Start Direct Line per call (single conversation)
            _dl = new CopilotDirectLineService(_config);
            try
            {
                await _dl.StartAsync(_callerId, _corrId, lct);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DirectLine] Start failed: {ex.Message}");
            }

            // 2) Background: stream bot replies -> TTS -> outbound audio
            _ = Task.Run(async () =>
            {
                if (_dl == null) return;
                try
                {
                    await foreach (var text in _dl.ReadBotRepliesAsync(lct))
                    {
                        if (!string.IsNullOrWhiteSpace(text))
                            _botReplies.Add(text, lct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DirectLine] Reply stream ended: {ex.Message}");
                }
            }, lct);

            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var text in _botReplies.GetConsumingEnumerable(lct))
                    {
                        await SpeakTextToCallWithBargeInAsync(text, lct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TTS] Speak loop error: {ex.Message}");
                }
            }, lct);

            // 3) Optional greeting (best effort, will be barged-in if user speaks)
            try
            {
                var greet = _config["TtsGreeting"];
                if (!string.IsNullOrWhiteSpace(greet))
                {
                    _ = Task.Run(() => SpeakTextToCallWithBargeInAsync(greet!, lct), lct);
                }
            }
            catch { /* non-fatal */ }

            // 4) Prepare STT recognizer with push stream
            _sttSession = _stt.CreatePcm16kMonoSession();
            WireRecognizerEvents(_sttSession.Recognizer, lct);
            await _sttSession.StartAsync().ConfigureAwait(false);

            // 5) Receive loop
            var rented = ArrayPool<byte>.Shared;
            var buf = rented.Rent(64 * 1024);
            try
            {
                while (!lct.IsCancellationRequested && _ws.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    var message = await ReceiveFullMessageAsync(_ws, buf, lct);
                    if (message == null) break;
                    var payload = TryDecodeUtf8(message.Value);
                    if (payload is null) continue;

                    try
                    {
                        var obj = JsonNode.Parse(payload) as JsonObject;
                        if (obj is null) continue;
                        var kind = obj["kind"]?.GetValue<string>()?.Trim();

                        switch (kind)
                        {
                            case "AudioMetadata":
                            case "audioMetadata":
                            {
                                var md = obj["audioMetadata"] as JsonObject;
                                if (md != null)
                                {
                                    _sampleRate = md["sampleRate"]?.GetValue<int?>() ?? _sampleRate;
                                    _channels   = md["channels"]?.GetValue<int?>() ?? _channels;
                                    Console.WriteLine($"[ACS] AudioMetadata: PCM, {_sampleRate} Hz, {_channels} ch");
                                    _metadataReady.TrySetResult(true);
                                }
                                break;
                            }
                            case "AudioData":
                            case "audioData":
                            {
                                var dataNode = obj["audioData"] as JsonObject;
                                if (dataNode == null) break;

                                bool silent = dataNode["silent"]?.GetValue<bool?>()
                                              ?? dataNode["isSilent"]?.GetValue<bool?>()
                                              ?? false;

                                var base64 = dataNode["data"]?.GetValue<string>();
                                if (!string.IsNullOrWhiteSpace(base64))
                                {
                                    try
                                    {
                                        byte[] pcm = Convert.FromBase64String(base64);
                                        await OnPcmChunk(pcm, _sampleRate, _channels, silent, lct);
                                    }
                                    catch (FormatException)
                                    {
                                        Console.WriteLine("[ACS] Skipped invalid base64 audioData frame.");
                                    }
                                }
                                break;
                            }
                            default:
                                Console.WriteLine($"[ACS] Unhandled message kind: '{kind}'");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ACS] JSON parse/handle failed: {ex.Message}");
                    }
                }
            }
            finally
            {
                rented.Return(buf);
                try { if (_ws.State == System.Net.WebSockets.WebSocketState.Open) await _ws.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", lct); } catch { }
                try { _botReplies.CompleteAdding(); } catch { }
                if (_sttSession is not null) await _sttSession.DisposeAsync();
            }
        }

        private void WireRecognizerEvents(Microsoft.CognitiveServices.Speech.SpeechRecognizer reco, CancellationToken ct)
        {
            // One-shot barge-in on speech start
            reco.SpeechStartDetected += async (_, __) => await EnsureBargeInAsync(ct);

            // Partials (optional)
            reco.Recognizing += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Result?.Text))
                    Console.WriteLine($"[STT] ~ {e.Result.Text}");
            };

            // Finals -> send to Copilot
            reco.Recognized += async (_, e) =>
            {
                var text = e.Result?.Text;
                if (string.IsNullOrWhiteSpace(text)) return;

                Console.WriteLine($"[STT] ✔ {text}");
                try
                {
                    if (_dl != null)
                        await _dl.PostUserTextAsync(text!, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DirectLine] Post failed: {ex.Message}");
                }
            };

            reco.Canceled += (_, e) =>
            {
                Console.WriteLine($"[STT] Canceled: {e.Reason} {e.ErrorDetails}");
            };
            reco.SessionStarted += (_, __) => Console.WriteLine("[STT] SessionStarted");
            reco.SessionStopped += (_, __) => Console.WriteLine("[STT] SessionStopped");
        }

        private async Task EnsureBargeInAsync(CancellationToken ct)
        {
            if (!_isSpeaking) return;

            bool shouldStop = false;
            lock (_speakLock)
            {
                if (_isSpeaking && !_stopSent)
                {
                    _stopSent = true;        // ensure single stopAudio per episode
                    _speakCts?.Cancel();     // cancel the TTS frame loop
                    shouldStop = true;
                }
            }
            if (shouldStop)
            {
                try { await StopAudioAsync(ct); } catch { /* non-fatal */ }
            }
        }

        private async Task OnPcmChunk(byte[] pcm, int sampleRate, int channels, bool silent, CancellationToken ct)
        {
            // Feed STT push stream (no extra stopAudio heuristics here)
            _sttSession?.PushPcm(pcm);
        }

        /// <summary>
        /// Speak text via Azure Speech TTS -> stream into call; supports cancellation for barge-in.
        /// Waits briefly until AudioMetadata arrives so ACS is outbound-ready.
        /// </summary>
        private async Task SpeakTextToCallWithBargeInAsync(string text, CancellationToken ct)
        {
            if (_ws.State != System.Net.WebSockets.WebSocketState.Open || string.IsNullOrWhiteSpace(text))
                return;

            // Wait up to 2s for AudioMetadata (ACS readiness)
            try { await Task.WhenAny(_metadataReady.Task, Task.Delay(2000, ct)); } catch { }

            CancellationTokenSource localCts;
            lock (_speakLock)
            {
                _speakCts?.Cancel(); // cancel any prior
                _speakCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                localCts = _speakCts;
                _isSpeaking = true;
                _stopSent = false; // reset at start of episode
            }

            try
            {
                await foreach (var frame in _tts.SpeakTextFramesAsync(
                    text,
                    frameSizeBytes: 640, // 20ms @16 kHz/16-bit mono
                    paceDelayMsPerFrame: 20,
                    ct: localCts.Token))
                {
                    if (localCts.IsCancellationRequested || ct.IsCancellationRequested) break;
                    await SendPcmToCallAsync(frame, ct);
                }
            }
            catch (OperationCanceledException) { /* expected on barge-in */ }
            finally
            {
                lock (_speakLock)
                {
                    _isSpeaking = false;
                    _stopSent = false; // allow stop for the next TTS
                }
            }
        }

        public async Task SendPcmToCallAsync(byte[] pcm, CancellationToken ct = default)
        {
            if (_ws.State != System.Net.WebSockets.WebSocketState.Open) return;
            var payload = OutStreamingData.GetAudioDataForOutbound(pcm);
            Console.WriteLine($"[WS→ACS] sending audioData {pcm.Length} bytes");
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _ws.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
        }

        public async Task StopAudioAsync(CancellationToken ct = default)
        {
            if (_ws.State != System.Net.WebSockets.WebSocketState.Open) return;
            var payload = OutStreamingData.GetStopAudioForOutbound();
            Console.WriteLine("[WS→ACS] stopAudio");
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _ws.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
        }

        private static async Task<ArraySegment<byte>?> ReceiveFullMessageAsync(System.Net.WebSockets.WebSocket ws, byte[] buf, CancellationToken ct)
        {
            var ms = new System.IO.MemoryStream();
            while (true)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) return null;
                ms.Write(buf, 0, result.Count);
                if (result.EndOfMessage) break;
            }
            return new ArraySegment<byte>(ms.ToArray());
        }

        private static string? TryDecodeUtf8(ArraySegment<byte> bytes)
        {
            try
            {
                var s = Encoding.UTF8.GetString(bytes.Array!, bytes.Offset, bytes.Count);
                return s.Length > 0 && (s[0] == '{' || s[0] == '[') ? s : null;
            }
            catch { return null; }
        }

        public async Task SendMessageAsync(string message, CancellationToken ct = default)
        {
            if (_ws.State != System.Net.WebSockets.WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(message);
            await _ws.SendAsync(bytes, System.Net.WebSockets.WebSocketMessageType.Text, true, ct);
        }
    }
}
