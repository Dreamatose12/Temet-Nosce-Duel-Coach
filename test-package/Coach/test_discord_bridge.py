import json
import unittest
from discord_bridge import chunks, post_webhook, DiscordError

class FakeResponse:
    def __init__(self, data): self.data = data
    def __enter__(self): return self
    def __exit__(self, *args): pass
    def read(self, limit): return json.dumps(self.data).encode()

class FakeOpener:
    def __init__(self): self.requests = []
    def open(self, request, timeout): self.requests.append(request); return FakeResponse({})

class BridgeTests(unittest.TestCase):
    def test_chunks(self): self.assertEqual(chunks("12345", 2), ["12", "34", "5"])
    def test_webhook_validation_and_mentions(self):
        opener = FakeOpener(); post_webhook("https://discord.com/api/webhooks/1/token", "report", opener=opener)
        self.assertEqual(json.loads(opener.requests[0].data)["allowed_mentions"], {"parse": []})
        with self.assertRaises(DiscordError): post_webhook("https://example.invalid/hook", "report")
if __name__ == "__main__": unittest.main()
