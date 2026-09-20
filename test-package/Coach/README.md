# Discord duel coach

This stdlib-only sidecar handles completed `DuelReports/*.jsonl` files. The server name is `Temet Nosce`; Discord API calls still require a numeric channel ID. Reports use a webhook; `!coach <question>` uses a bot token in the same channel. No credentials are stored in this repository.

Configure the launching process:

```powershell
$env:AO_DISCORD_CHANNEL_ID = '<numeric-channel-id>'
$env:AO_DISCORD_BOT_TOKEN = '<Discord bot token>'
$env:AO_DISCORD_WEBHOOK_URL = 'https://discord.com/api/webhooks/<id>/<token>'
$env:OPENAI_API_KEY = '<OpenAI API key>'
$env:AO_COACH_MODEL = '<your chosen API model>'
```

Publish the newest complete report:

```powershell
python .\discord_bridge.py 'C:\path\to\DuelReports' --publish
```

Poll once with `--poll --once`, or continuously with `--poll`. Add `--watch` to automatically publish each newly completed report; combine it with `--poll` for the full report-and-coaching loop. Players ask `!coach what should I practice next?`; coaching requests are throttled to one every 15 seconds. The sidecar answers only from the newest complete report, identifies unknowns, and sends no game actions. The Discord bot needs View Channel, Read Message History, and Send Messages in the configured channel.
