import unittest
from analyze import analyze, markdown


def event(kind, seconds, **data):
    return dict(sessionId="synthetic-test", kind=kind, elapsedSeconds=seconds, data=data)


def start():
    return event("start", 0, schema=1, botId=1, opponentId=2, mode="AiOverlord")


class AnalysisTests(unittest.TestCase):
    def test_incomplete_not_finished(self):
        self.assertFalse(analyze([start()])["complete"])

    def test_unknown_outcome_even_low_health(self):
        report = analyze([start(), event("sample", 1, targetHealthPercent=0), event("end", 2, reason="Stopped")])
        self.assertEqual(report["outcome"], "unknown")
        self.assertTrue(report["complete"])

    def test_observed_single_death_can_establish_outcome(self):
        report = analyze([start(), event("outcomeObservation", 1, outcome="opponent_won", evidence="client_alive_state")])
        self.assertEqual(report["outcome"], "opponent_won")

    def test_ambiguous_death_stays_unknown(self):
        report = analyze([start(), event("outcomeObservation", 1, outcome="unknown", evidence="client_alive_state")])
        self.assertEqual(report["outcome"], "unknown")

    def test_gaps_not_extrapolated(self):
        report = analyze([start(), event("sample", 1, targetBuffs=[dict(Name="Dance of Fools", RemainingTime=60)]),
                          event("sample", 1.25), event("sample", 20)])
        self.assertEqual(report["metrics"]["targetDofObservedSeconds"], .25)
        self.assertEqual(report["metrics"]["sampleGapSeconds"], 18.75)

    def test_health_increases_are_net(self):
        report = analyze([start(), event("sample", 1, targetHealth=100), event("sample", 1.25, targetHealth=150)])
        self.assertEqual(report["metrics"]["netHealthIncreases"], {"target": 50})
        self.assertIn("Not total healing", report["inferences"][0]["caveat"])

    def test_packets_scoped(self):
        report = analyze([start(), event("damagePacket", 1, actor=1, target=2, Amount=42),
                          event("damagePacket", 2, actor=3, target=2, Amount=500),
                          event("damagePacket", 3, actor=2, target=1, Amount=-50)])
        self.assertEqual(report["packetObservations"]["botToTarget"]["rawPositiveAmountSum"], 42)
        self.assertEqual(report["packetObservations"]["targetToBot"]["count"], 0)

    def test_bad_percent_not_used(self):
        report = analyze([start(), event("sample", 1, targetHealthPercent=1234567890)])
        self.assertIsNone(report["metrics"]["targetHealthPercentMinimum"])

    def test_stats_never_become_verified(self):
        report = analyze([start(), event("targetStat", 1, stat="Evade", raw=9999, quality="unverified_client_value")])
        self.assertEqual(report["statQualityCounts"]["unverified_client_value"], 1)
        self.assertEqual(report["inferences"], [])

    def test_request_not_execution(self):
        report = analyze([start(), event("actionRequested", 1, action="perk")])
        self.assertEqual(report["actionRequests"], {"perk": 1})
        self.assertNotIn("executed", report)

    def test_mixed_session_rejected(self):
        wrong = event("end", 1)
        wrong["sessionId"] = "another"
        with self.assertRaises(ValueError):
            analyze([start(), wrong])

    def test_clock_reversal_rejected(self):
        with self.assertRaises(ValueError):
            analyze([start(), event("sample", 2), event("sample", 1)])

    def test_after_end_rejected(self):
        with self.assertRaises(ValueError):
            analyze([start(), event("end", 1), event("sample", 2)])

    def test_dropped_warning_and_markdown(self):
        report = analyze([start(), event("end", 2, droppedRecords=4)])
        self.assertIn("4 dropped records", markdown(report))


if __name__ == "__main__":
    unittest.main()
