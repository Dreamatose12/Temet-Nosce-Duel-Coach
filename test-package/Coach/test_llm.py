import io
import json
import unittest
import urllib.error
from analyze import analyze
from test_analyze import event, start
from llm import answer, request_payload, CoachError


class FakeOpener:
    def __init__(self, data):
        self.data = data
        self.request = None

    def open(self, request, timeout):
        self.request = request
        self.timeout = timeout
        return io.BytesIO(json.dumps(self.data).encode())


class LlmTests(unittest.TestCase):
    def setUp(self):
        self.report = analyze([start(), event("end", 1)])

    def test_stateless_bounded_no_tools(self):
        payload = request_payload(self.report, "What should I practice?", "chosen-model")
        self.assertFalse(payload["store"])
        self.assertEqual(payload["max_output_tokens"], 2000)
        self.assertNotIn("tools", payload)
        self.assertNotIn("opponentName", json.loads(payload["input"])["evidence"])

    def test_missing_model_rejected(self):
        with self.assertRaises(ValueError):
            request_payload(self.report, "question", None)

    def test_incomplete_rejected(self):
        with self.assertRaises(ValueError):
            request_payload(analyze([start()]), "question", "chosen-model")

    def test_long_question_rejected(self):
        with self.assertRaises(ValueError):
            request_payload(self.report, "x" * 2001, "chosen-model")

    def test_output_searches_all_items(self):
        text = "\n".join(["Observed evidence", "Likely inference", "Unknowns", "Recommended practice drill", "Evidence that would confirm the hypothesis"])
        opener = FakeOpener({"status": "completed", "output": [{"type": "reasoning"},
            {"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": text}]}]})
        self.assertEqual(answer(self.report, "question", api_key="synthetic", model="test", opener=opener), text)
        self.assertEqual(opener.request.full_url, "https://api.openai.com/v1/responses")
        self.assertEqual(opener.timeout, 45)

    def test_truncated_answer_not_success(self):
        with self.assertRaises(CoachError):
            answer(self.report, "question", api_key="synthetic", model="test", opener=FakeOpener({"status": "incomplete"}))

    def test_refusal_is_not_empty_success(self):
        with self.assertRaises(CoachError):
            answer(self.report, "question", api_key="synthetic", model="test", opener=FakeOpener({"status": "completed", "output": []}))

    def test_unstructured_answer_rejected(self):
        with self.assertRaises(CoachError):
            answer(self.report, "question", api_key="synthetic", model="test", opener=FakeOpener({"status": "completed", "output": [{"type": "message", "role": "assistant", "content": [{"type": "output_text", "text": "one paragraph"}]}]}))


if __name__ == "__main__":
    unittest.main()
