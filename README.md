# Azure-Voice-To-CoPilot-Studio-Bot
Adds voice capability to an existing chat bot leveraging Azure Communication Services, Azure Cognitive Services

# Copilot Voice — Overlay (with Azure Speech TTS)

This overlay adds Azure Speech **TTS** streaming over the ACS WebSocket. You’ll hear a short greeting as soon as the call connects (optional), and you can reuse the service to speak any text back to the caller.

## Files included
- `Program.cs` — registers `SpeechTtsService` and passes it to `/ws` handler. (Also still answers, starts Copilot, and plays first reply.)
- `AcsMediaStreamingHandler.cs` — stable WS loop + **SpeakTextToCallAsync** that streams 16k/mono PCM back to the call; optional `TtsGreeting`.
- `SpeechTtsService.cs` — Azure Speech TTS wrapper (raw PCM @ 16 kHz mono, 640‑byte frames).
- `CallAutomation_AzureAI_VoiceLive.csproj` — adds `Microsoft.CognitiveServices.Speech` package.
- (`deploy.sh` patch) — picks up `SpeechTtsService.cs` in the overlay and sets `TtsGreeting` if desired.

## Settings
Required:
- `SpeechKey` and `SpeechRegion` (region of your Speech resource)

Recommended:
- `DefaultVoice` (e.g., `en-US-AriaNeural`)
- `TtsGreeting` (e.g., “You’re connected. This is a TTS smoke test.”) — optional

## Quick smoke test
1. Deploy and ensure app settings include `SpeechKey`, `SpeechRegion`, `DefaultVoice`, and optionally `TtsGreeting`.
2. `curl -I https://<app>.azurewebsites.net/healthz` — should be `200 OK`.
3. Place a test call → you should hear the `TtsGreeting`. If not:
   - Check WebSocket signature (if `WsSigningKey` is set).
   - Tail logs: `az webapp log tail -g <rg> -n <app>`
   - Verify `SpeechRegion` matches your Speech resource region.

## Notes
- The first Copilot reply still plays via `CallMedia.PlayToAllAsync(TextSource)`. Move to **SpeakTextToCallAsync** once you wire full STT→Copilot→TTS turn-taking.

- **New**: Live STT (Azure Speech) from ACS WS AudioData via push stream @16k/mono PCM.
- **New**: Barge‑in — on SpeechStartDetected, we send `stopAudio` and cancel current TTS so the caller can interrupt the bot.
- **New**: Recognized (final) user text → posted to Copilot via Direct Line → bot reply is streamed back via TTS.
