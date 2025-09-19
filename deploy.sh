#!/usr/bin/env bash
set -euo pipefail

# Optional: better debugging when needed
# if [[ "${DEBUG:-0}" == "1" ]]; then set -x; fi

# Ensure az extensions install silently if missing
az config set extension.use_dynamic_install=yes_without_prompt >/dev/null

# =========================
# Environment/config (use environment variables or configuration)
# =========================
SUBSCRIPTION_ID="${SUBSCRIPTION_ID:-}"
RESOURCE_GROUP="${RESOURCE_GROUP:-my-resource-group}"
LOCATION="${LOCATION:-westus2}"
APP_PLAN="${APP_PLAN:-my-app-plan}"
APP_NAME="${APP_NAME:-my-app}"

# Align SPEECH_REGION with your Speech resource region for best latency
SPEECH_REGION="${SPEECH_REGION:-westus}"
AZURE_AI_ENDPOINT="${AZURE_AI_ENDPOINT:-https://<your-cognitive-service>.cognitiveservices.azure.com/}"
VOICE_LOCALE="${VOICE_LOCALE:-en-US}"
EVENT_SUB_NAME="${EVENT_SUB_NAME:-acs-incomingcall-sub}"

# =========================
# Project settings
# =========================
PINNED_ABS_PROJECT="${PINNED_ABS_PROJECT:-/path/to/your/project.csproj}"
DEFAULT_REL_PROJECT="CallAutomation_AzureAI_VoiceLive/CallAutomation_AzureAI_VoiceLive/CallAutomation_AzureAI_VoiceLive.csproj"
SAMPLE_REPO_URL="${SAMPLE_REPO_URL:-https://github.com/Azure-Samples/communication-services-dotnet-quickstarts.git}"
GIT_BRANCH="${GIT_BRANCH:-main}"
CLONE_DIR="${CLONE_DIR:-$HOME/acs-samples}"

# =========================
# Secrets (prompt interactively; do NOT echo values)
# =========================
if [[ -z "${ACS_CONNECTION_STRING:-}" ]]; then
  read -rsp "ACS connection string: " ACS_CONNECTION_STRING && echo
fi
if [[ -z "${COPILOT_WEB_CHANNEL_KEY:-}" ]]; then
  read -rsp "Copilot Web Channel Security key (Direct Line secret): " COPILOT_WEB_CHANNEL_KEY && echo
fi
if [[ -z "${WS_SIGNING_KEY:-}" ]]; then
  read -rsp "WebSocket signing key (optional; press Enter to skip): " WS_SIGNING_KEY && echo
fi
if [[ -z "${SPEECH_KEY:-}" ]]; then
  read -rsp "Azure Speech key (optional): " SPEECH_KEY && echo
fi

# =========================
# Derived settings
# =========================
BASE_URI="https://${APP_NAME}.azurewebsites.net"
BASE_WSS_URI="${APP_NAME}.azurewebsites.net"
ACS_NAME=$(printf "%s" "$ACS_CONNECTION_STRING" | sed -E 's#.*https://([^.]*)\..*#\1#' || true)

# =========================
# Utilities (log to STDERR)
# =========================
log()   { printf "\n\033[1;34m[INFO]\033[0m %s\n" "$*" >&2; }
warn()  { printf "\n\033[1;33m[WARN]\033[0m %s\n" "$*" >&2; }
fail()  { printf "\n\033[1;31m[ERROR]\033[0m %s\n" "$*" >&2; exit 1; }
ensure_dir() { mkdir -p "$1"; }
ensure_providers() {
  log "Registering resource providers (idempotent)..."
  az provider register --namespace Microsoft.EventGrid --wait
  az provider register --namespace Microsoft.Communication --wait
}

# Resolve the project path
resolve_project_path() {
  local p="$PINNED_ABS_PROJECT"
  if [[ -f "$p" ]]; then
    echo "$p"; return 0
  fi
  log "Pinned project not found at $p"
  log "Cloning samples repo into $CLONE_DIR ..."
  ensure_dir "$(dirname "$CLONE_DIR")"
  if [[ ! -d "$CLONE_DIR/.git" ]]; then
    git clone --depth 1 --branch "$GIT_BRANCH" "$SAMPLE_REPO_URL" "$CLONE_DIR"
  else
    log "Repo already exists; updating..."
    git -C "$CLONE_DIR" fetch --depth 1 origin "$GIT_BRANCH" || true
    git -C "$CLONE_DIR" checkout "$GIT_BRANCH" || true
    git -C "$CLONE_DIR" pull --ff-only || true
  fi
  local rel="$CLONE_DIR/$DEFAULT_REL_PROJECT"
  if [[ -f "$rel" ]]; then
    echo "$rel"; return 0
  fi
  return 1
}

check_tfm_matches_site() {
  local csproj="$1"
  local tfm
  tfm="$(grep -oPm1 '(?<=<TargetFramework>)[^<]+' "$csproj" || true)"
  if [[ -z "$tfm" ]]; then
    tfm="$(grep -oPm1 '(?<=<TargetFrameworks>)[^<]+' "$csproj" | sed 's/[;, ]/\n/g' | head -n1 || true)"
  fi
  if [[ -z "$tfm" ]]; then
    warn "Could not detect <TargetFramework> in $csproj"
    return 0
  fi
  log "Detected project TargetFramework: $tfm"
  case "$tfm" in
    net8.0|net8.0-*) log "Project targets .NET 8 — aligned with DOTNETCORE|8.0 on App Service." ;;
    net9.0|net9.0-*) warn "Project targets .NET 9; site is configured for DOTNETCORE|8.0. Consider retargeting or deploying self-contained." ;;
    *) warn "Project TFM ($tfm) may not match App Service runtime DOTNETCORE|8.0." ;;
  esac
}

# ========== Overlay files ==========
OVERLAY_DIR="${OVERLAY_DIR:-/path/to/overlay}"
APPLY_OVERLAY=${APPLY_OVERLAY:-1}
PROJECT_DIR=""

declare -A OVERLAY_FILES=(
  ["Registry.cs"]="Registry.cs"
  ["Helpers.cs"]="Helpers.cs"
  ["AcsMediaStreamingHandler.cs"]="AcsMediaStreamingHandler.cs"
  ["Program.cs"]="Program.cs"
  ["CallAutomation_AzureAI_VoiceLive.csproj"]="CallAutomation_AzureAI_VoiceLive.csproj"
  ["CopilotDirectLineService.cs"]="CopilotDirectLineService.cs"
  ["SpeechTtsService.cs"]="SpeechTtsService.cs"
  ["SpeechSttService.cs"]="SpeechSttService.cs"
)

apply_overlay() {
  rm -f "$PROJECT_DIR/Helper.cs"
  local proj_dir="$1"
  if [[ "$APPLY_OVERLAY" != "1" ]]; then
    log "Overlay disabled (APPLY_OVERLAY=$APPLY_OVERLAY). Skipping overlay copies."
    return 0
  fi
  if [[ ! -d "$OVERLAY_DIR" ]]; then
    warn "Overlay directory $OVERLAY_DIR not found; skipping."
    return 0
  fi
  local backup_dir="$proj_dir/.overlay-backups/$(date '+%Y%m%d-%H%M%S')"
  mkdir -p "$backup_dir"
  log "Applying overlay from $OVERLAY_DIR → $proj_dir"
  for src_name in "${!OVERLAY_FILES[@]}"; do
    local src="$OVERLAY_DIR/$src_name"
    local dest_rel="${OVERLAY_FILES[$src_name]}"
    local dest="$proj_dir/$dest_rel"
    if [[ ! -f "$src" ]]; then continue; fi
    mkdir -p "$(dirname "$dest")"
    if [[ -f "$dest" ]]; then cp -p "$dest" "$backup_dir/"; fi
    cp -p "$src" "$dest"
    log "Overlayed: $dest_rel (from $src_name)"
  done
  log "Overlay complete. Backups (if any) in: $backup_dir"
}

# =========================
# Azure setup
# =========================
az account set --subscription "$SUBSCRIPTION_ID"
ensure_providers

# RG & Plan
az group show -n "$RESOURCE_GROUP" >/dev/null || az group create -n "$RESOURCE_GROUP" -l "$LOCATION"
az appservice plan show -g "$RESOURCE_GROUP" -n "$APP_PLAN" >/dev/null || az appservice plan create -g "$RESOURCE_GROUP" -n "$APP_PLAN" --is-linux --sku P1v3 --location "$LOCATION"

# Create app, then force DOTNETCORE|8.0
if ! az webapp show -g "$RESOURCE_GROUP" -n "$APP_NAME" >/dev/null 2>&1; then
  log "Creating Web App with Linux runtime..."
  if ! az webapp create -g "$RESOURCE_GROUP" -n "$APP_NAME" --plan "$APP_PLAN" --runtime "DOTNETCORE|8.0"; then
    warn "Create failed with DOTNETCORE|8.0; showing available runtimes:"
    az webapp list-runtimes --os linux || true
    warn "Retrying create without --runtime (will set linuxFxVersion next)..."
    az webapp create -g "$RESOURCE_GROUP" -n "$APP_NAME" --plan "$APP_PLAN"
  fi
fi

# Remove any container config
set +e
az webapp config container show -g "$RESOURCE_GROUP" -n "$APP_NAME" -o tsv >/dev/null 2>&1
HAS_CONTAINER_CFG=$?
set -e
if [[ $HAS_CONTAINER_CFG -eq 0 ]]; then
  log "Removing container configuration (switching to Code stack)..."
  az webapp config container delete -g "$RESOURCE_GROUP" -n "$APP_NAME" -o none
fi

# Force Linux .NET 8 runtime + enable websockets + always-on
az webapp config set -g "$RESOURCE_GROUP" -n "$APP_NAME" \
  --linux-fx-version "DOTNETCORE|8.0" \
  --web-sockets-enabled true \
  --always-on true
RUNTIME=$(az webapp config show -g "$RESOURCE_GROUP" -n "$APP_NAME" --query linuxFxVersion -o tsv)
log "linuxFxVersion: $RUNTIME"
[[ "$RUNTIME" == "DOTNETCORE|8.0" ]] || fail "Could not set DOTNETCORE|8.0 on the site."

# HTTPS-only
az webapp update -g "$RESOURCE_GROUP" -n "$APP_NAME" --https-only true -o none

# =========================
# App settings
# =========================
az webapp config appsettings set -g "$RESOURCE_GROUP" -n "$APP_NAME" --settings \
  AcsConnectionString="$ACS_CONNECTION_STRING" \
  CognitiveServiceEndpoint="$AZURE_AI_ENDPOINT" \
  DirectLineSecret="$COPILOT_WEB_CHANNEL_KEY" \
  BaseUri="$BASE_URI" \
  BaseWssUri="$BASE_WSS_URI" \
  VOICE_LOCALE="$VOICE_LOCALE" \
  DefaultVoice="en-GB-SoniaNeural" \
  InitialPrompt="Hello, thank you for calling. I am a virtual assistant." \
  TtsGreeting="You are connected. This is a TTS test greeting." \
  $( [[ -n "${WS_SIGNING_KEY:-}" ]] && printf "WsSigningKey=%q" "$WS_SIGNING_KEY" ) \
  $( [[ -n "${SPEECH_KEY:-}" ]] && printf "SpeechKey=%q SpeechRegion=%q" "$SPEECH_KEY" "$SPEECH_REGION" ) \
  -o none

# Legacy/uppercase keys for compatibility
az webapp config appsettings set -g "$RESOURCE_GROUP" -n "$APP_NAME" --settings \
  ACS_CONNECTION_STRING="$ACS_CONNECTION_STRING" \
  COGNITIVE_SERVICES_ENDPOINT="$AZURE_AI_ENDPOINT" \
  COPILOT_WEB_CHANNEL_KEY="$COPILOT_WEB_CHANNEL_KEY" \
  BASE_URI="$BASE_URI" \
  BASE_WSS_URI="$BASE_WSS_URI" \
  $( [[ -n "${WS_SIGNING_KEY:-}" ]] && printf "WS_SIGNING_KEY=%q" "$WS_SIGNING_KEY" ) \
  $( [[ -n "${SPEECH_KEY:-}" ]] && printf "SPEECH_KEY=%q SPEECH_REGION=%q" "$SPEECH_KEY" "$SPEECH_REGION" ) \
  -o none

# Linux health ping & startup timing
az webapp config appsettings set -g "$RESOURCE_GROUP" -n "$APP_NAME" --settings \
  ASPNETCORE_URLS="http://0.0.0.0:8080" \
  WEBSITES_PORT="8080" \
  WEBSITES_CONTAINER_START_TIME_LIMIT="1800" \
  WEBSITE_RUN_FROM_PACKAGE="1" \
  ASPNETCORE_ENVIRONMENT="Production" \
  -o none

# =========================
# Build & Deploy
# =========================
log "Resolving project..."
PROJECT_FILE_RESOLVED="$(resolve_project_path)" || fail "Pinned project not found and clone fallback also failed."
log "Using project: $PROJECT_FILE_RESOLVED"
check_tfm_matches_site "$PROJECT_FILE_RESOLVED"
PROJECT_DIR="$(dirname "$PROJECT_FILE_RESOLVED")"

# Apply overlay
apply_overlay "$PROJECT_DIR"

BUILD_ROOT="$PROJECT_DIR"
BUILD_OUTPUT="$BUILD_ROOT/publish"
ZIP_PATH="$BUILD_OUTPUT/site.zip"
log "Publishing project..."
dotnet restore "$PROJECT_FILE_RESOLVED"
dotnet publish "$PROJECT_FILE_RESOLVED" -c Release -o "$BUILD_OUTPUT"
log "Zipping published output..."
( cd "$BUILD_OUTPUT" && zip -qr "$ZIP_PATH" . )
log "Deploying to App Service..."
az webapp deploy -g "$RESOURCE_GROUP" -n "$APP_NAME" --src-path "$ZIP_PATH" --type zip
az webapp restart -g "$RESOURCE_GROUP" -n "$APP_NAME"

# Enable logs
az webapp log config -g "$RESOURCE_GROUP" -n "$APP_NAME" \
  --application-logging filesystem --web-server-logging filesystem --level information -o none

# =========================
# Event Grid subscription
# =========================
ACS_RESOURCE_ID=$(az resource list \
  --name "${ACS_NAME:-}" \
  --resource-type "Microsoft.Communication/communicationServices" \
  --subscription "$SUBSCRIPTION_ID" \
  --query "[0].id" -o tsv || true)
if [[ -z "${ACS_RESOURCE_ID:-}" ]]; then
  echo "Enter ACS resource id (/subscriptions/.../resourceGroups/.../providers/Microsoft.Communication/communicationServices/<name>):"
  read -r ACS_RESOURCE_ID
fi
set +e
log "Creating Event Grid subscription for Microsoft.Communication.IncomingCall..."
az eventgrid event-subscription create -o none \
  --name "$EVENT_SUB_NAME" \
  --source-resource-id "$ACS_RESOURCE_ID" \
  --endpoint "${BASE_URI}/api/incomingCall" \
  --included-event-types Microsoft.Communication.IncomingCall \
  --event-delivery-schema EventGridSchema
RC=$?
set -e
if [[ $RC -ne 0 ]]; then
  warn "Create failed—attempting to update existing subscription endpoint..."
  az eventgrid event-subscription update -o none \
    --name "$EVENT_SUB_NAME" \
    --source-resource-id "$ACS_RESOURCE_ID" \
    --endpoint "${BASE_URI}/api/incomingCall"
fi

echo
echo "✅ Done."
echo "Logs are enabled. Tail them to watch validation and first requests:"
echo " az webapp log tail -g $RESOURCE_GROUP -n $APP_NAME"
echo
echo "Check Event Grid subscription state (should be 'Succeeded'):"
echo " az eventgrid event-subscription show --name $EVENT_SUB_NAME --source-resource-id $ACS_RESOURCE_ID --query provisioningState -o tsv"