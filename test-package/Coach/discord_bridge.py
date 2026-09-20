"""Small stdlib-only Discord bridge for finished DuelRecorder reports."""
import argparse
import json
import os
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

from analyze import analyze, markdown, read_records
from llm import CoachError, answer

DISCORD_API = "https://discord.com/api/v10"
MAX_MESSAGE = 1900
COACH_INTERVAL_SECONDS = 15

class DiscordError(RuntimeError):
    pass

def _json_request(url, *, token=None, method="GET", data=None, timeout=30, opener=None):
    headers = {"User-Agent": "AO-Duel-Coach/1.0"}
    if token: headers["Authorization"] = "Bot " + token
    if data is not None: headers["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=json.dumps(data).encode() if data is not None else None,
                                     headers=headers, method=method)
    try:
        with (opener or urllib.request.build_opener()).open(request, timeout=timeout) as response:
            raw = response.read(1024 * 1024 + 1)
    except urllib.error.HTTPError as error:
        raise DiscordError(f"Discord returned HTTP {error.code}") from None
    except (urllib.error.URLError, TimeoutError, OSError):
        raise DiscordError("Discord connection failed") from None
    if len(raw) > 1024 * 1024: raise DiscordError("Discord response exceeded size limit")
    try: return json.loads(raw) if raw else None
    except ValueError: raise DiscordError("Discord returned invalid JSON") from None

def chunks(text, limit=MAX_MESSAGE):
    text = text.strip()
    return [text[i:i + limit] for i in range(0, len(text), limit)] or ["(empty report)"]

def post_webhook(webhook_url, text, *, opener=None):
    parsed = urllib.parse.urlparse(webhook_url)
    if parsed.scheme != "https" or parsed.netloc not in ("discord.com", "discordapp.com"):
        raise DiscordError("Webhook URL must be an HTTPS Discord webhook")
    for part in chunks(text):
        _json_request(webhook_url, method="POST", data={"content": part, "allowed_mentions": {"parse": []}}, opener=opener)

def get_messages(channel_id, token, *, after=None, opener=None):
    if not channel_id.isdigit(): raise DiscordError("AO_DISCORD_CHANNEL_ID must be numeric")
    query = {"limit": "50"}
    if after: query["after"] = after
    url = DISCORD_API + "/channels/" + channel_id + "/messages?" + urllib.parse.urlencode(query)
    messages = _json_request(url, token=token, opener=opener)
    if not isinstance(messages, list): raise DiscordError("Discord message response was not a list")
    return sorted((m for m in messages if isinstance(m, dict)), key=lambda m: m.get("id", ""))

def send_channel(channel_id, token, text, *, opener=None):
    if not channel_id.isdigit(): raise DiscordError("AO_DISCORD_CHANNEL_ID must be numeric")
    for part in chunks(text):
        _json_request(DISCORD_API + "/channels/" + channel_id + "/messages", token=token,
                      method="POST", data={"content": part, "allowed_mentions": {"parse": []}}, opener=opener)

def latest_report(directory):
    paths = sorted(Path(directory).glob("*.jsonl"), key=lambda p: p.stat().st_mtime, reverse=True)
    for path in paths:
        try:
            report = analyze(read_records(path))
            if report["complete"]: return path, report
        except (ValueError, OSError): continue
    raise DiscordError("No complete DuelRecorder report found")

def publish(directory, *, webhook=None, opener=None):
    path, report = latest_report(directory)
    text = "**AO duel report**\n```text\n" + markdown(report) + "\n```"
    text += "\nEvidence is client-observation based; outcome remains unknown unless independently recorded."
    if webhook: post_webhook(webhook, text, opener=opener)
    return path, report, text

def _state(path):
    try:
        value = json.loads(Path(path).read_text(encoding="utf-8")); return value if isinstance(value, dict) else {}
    except (OSError, ValueError): return {}

def poll_once(reports, channel_id, token, state_path, *, opener=None):
    state = _state(state_path)
    messages = get_messages(channel_id, token, after=state.get("lastMessageId"), opener=opener)
    path, report = latest_report(reports)
    for message in messages:
        state["lastMessageId"] = message.get("id", state.get("lastMessageId"))
        author = message.get("author") or {}
        if author.get("bot") or author.get("id") == state.get("botUserId"): continue
        content = message.get("content", "").strip()
        if not content.lower().startswith("!coach "): continue
        now = time.time()
        if now < float(state.get("lastCoachAt", 0)) + COACH_INTERVAL_SECONDS:
            if now >= float(state.get("lastRateNoticeAt", 0)) + COACH_INTERVAL_SECONDS:
                send_channel(channel_id, token, "Coach is rate-limited; please wait before asking another question.", opener=opener)
                state["lastRateNoticeAt"] = now
            continue
        try: response = answer(report, content[7:].strip())
        except (ValueError, CoachError) as error: response = "Coach unavailable: " + str(error)
        send_channel(channel_id, token, response, opener=opener)
        state["lastCoachAt"] = now
    Path(state_path).write_text(json.dumps(state, indent=2), encoding="utf-8")
    return path, len(messages)

def watch_once(reports, webhook, state_path, *, opener=None):
    if not webhook: raise DiscordError("Set AO_DISCORD_WEBHOOK_URL for report watch")
    state = _state(state_path)
    path, report, _ = publish(reports, opener=opener)
    if state.get("publishedSessionId") != report["sessionId"]:
        publish(reports, webhook=webhook, opener=opener)
        state["publishedSessionId"] = report["sessionId"]
        Path(state_path).write_text(json.dumps(state, indent=2), encoding="utf-8")
        return path, True
    return path, False

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reports"); parser.add_argument("--publish", action="store_true")
    parser.add_argument("--poll", action="store_true"); parser.add_argument("--watch", action="store_true")
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    webhook = os.environ.get("AO_DISCORD_WEBHOOK_URL")
    channel, token = os.environ.get("AO_DISCORD_CHANNEL_ID"), os.environ.get("AO_DISCORD_BOT_TOKEN")
    state = os.environ.get("AO_DISCORD_STATE", str(Path(args.reports) / "discord-state.json"))
    try:
        if args.publish:
            path, _, _ = publish(args.reports, webhook=webhook)
            print("Published report: " + str(path)) if webhook else print("Report prepared: " + str(path))
        if args.watch or args.poll:
            if args.watch and not webhook: raise DiscordError("Set AO_DISCORD_WEBHOOK_URL for report watch")
            if args.poll and (not channel or not token): raise DiscordError("Set AO_DISCORD_CHANNEL_ID and AO_DISCORD_BOT_TOKEN")
            while True:
                if args.watch:
                    path, published = watch_once(args.reports, webhook, state)
                    print(f"Checked report {path}; published={published}")
                if args.poll:
                    path, count = poll_once(args.reports, channel, token, state)
                    print(f"Checked {count} Discord messages; report={path}")
                if args.once: break
                time.sleep(15)
    except (DiscordError, ValueError, OSError) as error: parser.exit(2, "Discord bridge unavailable: " + str(error) + "\n")

if __name__ == "__main__": main()
