using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace CallAutomation.AzureAI.VoiceLive
{
    public class CopilotDirectLineService
    {
        private readonly HttpClient _http;
        private readonly string _directLineSecret;
        private readonly string _initialPrompt;
        private readonly string _defaultVoice;
        private readonly string _locale;
        private string? _directLineToken; // Cached, short-lived
        public string? ConversationId { get; private set; }
        public string? StreamUrl { get; private set; }

        public CopilotDirectLineService(IConfiguration config)
        {
            _directLineSecret = config["DirectLineSecret"] ?? throw new ArgumentNullException(nameof(config), "DirectLineSecret is required.");
            _initialPrompt = config["InitialPrompt"] ?? string.Empty;
            _defaultVoice = config["DefaultVoice"] ?? "en-US-AriaNeural";
            _locale = config["VOICE_LOCALE"] ?? "en-US";
            _http = new HttpClient();
        }

        private async Task<string> EnsureDirectLineTokenAsync(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(_directLineToken))
                return _directLineToken!;

            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://directline.botframework.com/v3/directline/tokens/generate");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _directLineSecret);
            var res = await _http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!.AsObject();
            _directLineToken = json["token"]!.GetValue<string>();
            return _directLineToken!;
        }

        public async Task StartAsync(string? callerId, string? correlationId, CancellationToken ct = default)
        {
            var token = await EnsureDirectLineTokenAsync(ct);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var resp = await _http.PostAsync("https://directline.botframework.com/v3/directline/conversations", null, ct);
            resp.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!.AsObject();
            ConversationId = json["conversationId"]!.GetValue<string>();
            StreamUrl = json["streamUrl"]?.GetValue<string>();

            // Send startConversation event with context
            var startEvt = new
            {
                type = "event",
                name = "startConversation",
                from = new { id = "voice-gateway" }, // Generalized ID
                channelData = new
                {
                    source = "VoiceChannel",
                    callerId,
                    correlationId,
                    scenario = "VoiceAssistant", // Generalized scenario
                    locale = _locale
                }
            };
            await PostActivityAsync(startEvt, ct);

            if (!string.IsNullOrWhiteSpace(_initialPrompt))
            {
                await PostActivityAsync(new
                {
                    type = "message",
                    from = new { id = "voice-gateway" }, // Generalized ID
                    text = _initialPrompt
                }, ct);
            }
        }

        public async Task PostUserTextAsync(string text, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(ConversationId)) return;
            var activity = new { type = "message", from = new { id = "user" }, text };
            await PostActivityAsync(activity, ct);
        }

        public async IAsyncEnumerable<string> ReadBotRepliesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            var triedWebSocket = false;

            // Prefer Direct Line streaming (WS)
            if (!string.IsNullOrWhiteSpace(StreamUrl))
            {
                triedWebSocket = true;
                var token = await EnsureDirectLineTokenAsync(ct);
                using var ws = new ClientWebSocket();
                ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                bool connected = false;
                try
                {
                    await ws.ConnectAsync(new Uri(StreamUrl!), ct);
                    connected = true;
                }
                catch (Exception ex)
                {
                    // Log to ILogger in production instead of Console
                }

                if (connected)
                {
                    var buf = new byte[8192];
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var sb = new StringBuilder();
                        WebSocketReceiveResult res;
                        do
                        {
                            res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                            if (res.MessageType == WebSocketMessageType.Close)
                            {
                                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct); } catch { }
                                break;
                            }
                            sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                        } while (!res.EndOfMessage && ws.State == WebSocketState.Open && !ct.IsCancellationRequested);

                        if (sb.Length > 0)
                        {
                            foreach (var text in ExtractBotTexts(sb.ToString()))
                                yield return text;
                        }
                    }
                }
            }

            // Polling fallback (or if WS not available)
            string? watermark = null;
            while (!ct.IsCancellationRequested)
            {
                var token = await EnsureDirectLineTokenAsync(ct);
                using var req = new HttpRequestMessage(HttpMethod.Get, BuildActivitiesUrl(ConversationId!, watermark));
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var resp = await _http.SendAsync(req, ct);

                if ((int)resp.StatusCode == 401 && triedWebSocket)
                {
                    _directLineToken = null;
                    var fresh = await EnsureDirectLineTokenAsync(ct);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
                    resp = await _http.SendAsync(req, ct);
                }

                resp.EnsureSuccessStatusCode();
                var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!.AsObject();
                watermark = j["watermark"]?.GetValue<string>();

                foreach (var text in ExtractBotTexts(j))
                    yield return text;

                await Task.Delay(500, ct);
            }
        }

        private static string BuildActivitiesUrl(string conversationId, string? watermark)
        {
            var baseUrl = $"https://directline.botframework.com/v3/directline/conversations/{conversationId}/activities";
            return string.IsNullOrEmpty(watermark) ? baseUrl : $"{baseUrl}?watermark={watermark}";
        }

        /// <summary>
        /// Treat any non-user "message" as a bot reply and prefer the "speak" field when present.
        /// Sanitizes the text to ensure it's either plain text or valid SSML.
        /// </summary>
        private static IEnumerable<string> ExtractBotTexts(string activitiesPayload)
        {
            var results = new List<string>();
            var payload = JsonNode.Parse(activitiesPayload);
            if (payload is JsonObject obj && obj["activities"] is JsonArray acts)
            {
                foreach (var a in acts)
                {
                    var type = a?["type"]?.GetValue<string>();
                    if (!string.Equals(type, "message", StringComparison.OrdinalIgnoreCase)) continue;

                    var from = a?["from"]?["id"]?.GetValue<string>() ?? "";
                    if (string.Equals(from, "user", StringComparison.OrdinalIgnoreCase)) continue;

                    var speak = a?["speak"]?.GetValue<string>();
                    var text = a?["text"]?.GetValue<string>();
                    var val = !string.IsNullOrWhiteSpace(speak) ? speak : text;

                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        val = SanitizeText(val, "en-US-AriaNeural");
                        results.Add(val!);
                    }
                }
            }
            return results;
        }

        private static IEnumerable<string> ExtractBotTexts(JsonNode? activitiesEnvelope)
        {
            var results = new List<string>();
            if (activitiesEnvelope is JsonObject j && j["activities"] is JsonArray acts)
            {
                foreach (var a in acts)
                {
                    var type = a?["type"]?.GetValue<string>();
                    if (!string.Equals(type, "message", StringComparison.OrdinalIgnoreCase)) continue;

                    var from = a?["from"]?["id"]?.GetValue<string>() ?? "";
                    if (string.Equals(from, "user", StringComparison.OrdinalIgnoreCase)) continue;

                    var speak = a?["speak"]?.GetValue<string>();
                    var text = a?["text"]?.GetValue<string>();
                    var val = !string.IsNullOrWhiteSpace(speak) ? speak : text;

                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        val = SanitizeText(val, "en-US-AriaNeural");
                        results.Add(val!);
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// Sanitizes the input text to ensure it's either plain text or valid SSML.
        /// Strips SSML-like tags if not valid SSML, or wraps incomplete SSML in proper tags.
        /// </summary>
        private static string SanitizeText(string text, string voiceName)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            // Detect if input is SSML (starts with <speak>)
            bool isSsml = text.Trim().StartsWith("<speak", StringComparison.OrdinalIgnoreCase);
            if (!isSsml && text.Contains("<"))
            {
                // Strip SSML-like tags for plain text
                text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "");
            }
            else if (isSsml && !text.Contains("xmlns=\"http://www.w3.org/2001/10/synthesis\""))
            {
                // Wrap incomplete SSML
                text = $"<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-US\"><voice name=\"{voiceName}\">{text}</voice></speak>";
            }
            return text;
        }

        private async Task PostActivityAsync(object activity, CancellationToken ct)
        {
            var token = await EnsureDirectLineTokenAsync(ct);
            var url = $"https://directline.botframework.com/v3/directline/conversations/{ConversationId}/activities";
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(activity), Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await _http.SendAsync(req, ct);
            if ((int)resp.StatusCode == 401)
            {
                // One refresh attempt
                _directLineToken = null;
                var fresh = await EnsureDirectLineTokenAsync(ct);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
                resp = await _http.SendAsync(req, ct);
            }
            resp.EnsureSuccessStatusCode();
        }
    }
}