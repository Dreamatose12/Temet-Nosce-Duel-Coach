"""Offline evidence extraction for schema-1 DuelRecorder JSONL. No network or SDK."""
import argparse
from collections import Counter
import json
import math
from pathlib import Path

MAX_BYTES = 64 * 1024 * 1024
MAX_RECORDS = 200000


def number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def read_records(path):
    path = Path(path)
    if path.stat().st_size > MAX_BYTES:
        raise ValueError("Recording exceeds 64 MiB analysis limit")
    records = []
    with path.open(encoding="utf-8-sig") as source:
        for line_no, line in enumerate(source, 1):
            if line_no > MAX_RECORDS or len(line) > 1024 * 1024:
                raise ValueError("Recording exceeds record limits")
            try:
                record = json.loads(line)
            except (ValueError, TypeError) as error:
                raise ValueError(f"Invalid JSON at line {line_no}; recording may still be active") from error
            records.append(record)
    return records


def analyze(records):
    if not records or not isinstance(records[0], dict) or records[0].get("kind") != "start":
        raise ValueError("Recording must start with session metadata")
    session = records[0].get("sessionId")
    if not isinstance(session, str) or not session or len(session) > 128:
        raise ValueError("Invalid session ID")
    meta = records[0].get("data", {})
    if not isinstance(meta, dict) or meta.get("schema") != 1:
        raise ValueError("Unsupported recording schema")
    previous = -1
    for index, record in enumerate(records):
        if not isinstance(record, dict) or record.get("sessionId") != session or not isinstance(record.get("data"), dict):
            raise ValueError("Mixed sessions or malformed record")
        timestamp = record.get("elapsedSeconds")
        if not number(timestamp) or timestamp < previous:
            raise ValueError("Non-monotonic or invalid elapsed time")
        if index and record.get("kind") == "start":
            raise ValueError("Duplicate session start")
        if record.get("kind") == "end" and index != len(records) - 1:
            raise ValueError("Records follow session end")
        previous = timestamp
    ended = records[-1].get("kind") == "end"
    end = records[-1]["data"] if ended else {}
    samples = [r for r in records if r.get("kind") == "sample"]
    outcome_events = [r["data"] for r in records if r.get("kind") == "outcomeObservation"
                      and r["data"].get("outcome") in ("bot_won", "opponent_won")
                      and r["data"].get("evidence") == "client_alive_state"]
    outcomes = {e["outcome"] for e in outcome_events}
    outcome = next(iter(outcomes)) if len(outcomes) == 1 else "unknown"
    warnings = ["Target stats are unverified client values; no hidden gear or server stats are inferred.",
                "Packet actor/amount semantics require live validation; packet sums are not verified DPS.",
                "Ordinary stops, timeouts, and ambiguous death states do not establish a winner."]
    if outcome != "unknown":
        warnings.append("Outcome is based only on an observed client alive-state event, not an inferred health threshold.")
    if not ended:
        warnings.append("Incomplete recording: no terminal record. Do not publish as a finished duel.")
    dropped = end.get("droppedRecords")
    if dropped:
        warnings.append(f"Recorder reported {dropped} dropped records; coverage is incomplete.")
    if end.get("writerError"):
        warnings.append("Recorder reported a writer error.")
    metrics = {"sampleCount": len(samples), "sessionElapsedSeconds": records[-1]["elapsedSeconds"],
               "sampleSpanSeconds": samples[-1]["elapsedSeconds"] - samples[0]["elapsedSeconds"] if len(samples) > 1 else 0,
               "observedIntervalSeconds": 0.0, "sampleGapSeconds": 0.0,
               "targetDofObservedSeconds": 0.0, "targetBelow25ObservedSeconds": 0.0,
               "botBelow25ObservedSeconds": 0.0, "distanceOver20ObservedSeconds": 0.0}
    for field in ("localHealthPercent", "targetHealthPercent", "localNanoPercent"):
        values = [r["data"].get(field) for r in samples]
        valid = [v for v in values if number(v) and 0 <= v <= 100]
        metrics[field + "Minimum"] = min(valid) if valid else None
    recoveries = Counter()
    for left, right in zip(samples, samples[1:]):
        delta = right["elapsedSeconds"] - left["elapsedSeconds"]
        if delta > 1:
            metrics["sampleGapSeconds"] += delta
            continue
        metrics["observedIntervalSeconds"] += delta
        data = left["data"]
        buffs = data.get("targetBuffs", [])
        if isinstance(buffs, list) and any(isinstance(b, dict) and b.get("Name") == "Dance of Fools"
                                         and number(b.get("RemainingTime")) and b["RemainingTime"] > 0 for b in buffs):
            metrics["targetDofObservedSeconds"] += delta
        for source, dest in (("targetHealthPercent", "targetBelow25ObservedSeconds"), ("localHealthPercent", "botBelow25ObservedSeconds")):
            value = data.get(source)
            if number(value) and 0 <= value <= 25:
                metrics[dest] += delta
        distance = data.get("distance")
        if number(distance) and distance > 20:
            metrics["distanceOver20ObservedSeconds"] += delta
        for side in ("local", "target"):
            a, b = data.get(side + "Health"), right["data"].get(side + "Health")
            if number(a) and number(b) and 0 <= a < b:
                recoveries[side] += b - a
    if metrics["sampleGapSeconds"]:
        warnings.append("Long sample gaps are excluded from buff/health/range duration estimates.")
    metrics["netHealthIncreases"] = dict(recoveries)
    packets = {"botToTarget": {"count": 0, "rawPositiveAmountSum": 0}, "targetToBot": {"count": 0, "rawPositiveAmountSum": 0}}
    stats, decisions, actions = Counter(), Counter(), Counter()
    equipment = []
    for record in records:
        kind, data = record.get("kind"), record["data"]
        if kind == "damagePacket":
            pair = data.get("actor"), data.get("target")
            lane = "botToTarget" if pair == (meta.get("botId"), meta.get("opponentId")) else "targetToBot" if pair == (meta.get("opponentId"), meta.get("botId")) else None
            amount = data.get("Amount")
            if lane and number(amount) and amount >= 0:
                packets[lane]["count"] += 1
                packets[lane]["rawPositiveAmountSum"] += amount
        elif kind == "targetStat":
            stats[str(data.get("quality", "unknown"))[:100]] += 1
        elif kind == "decision":
            decisions[str(data.get("reason", "unknown"))[:500]] += 1
        elif kind == "actionRequested":
            actions[str(data.get("action", "unknown"))[:200]] += 1
        elif kind in ("equipmentRequested", "equipmentResult", "equipmentReadiness"):
            equipment.append({"seconds": record["elapsedSeconds"], "kind": kind, "data": data})
    inferences = []
    if metrics["targetDofObservedSeconds"]:
        inferences.append({"claim": "An observed evasion window provided an opportunity to defer evadable perks.",
                           "evidence": ["metrics.targetDofObservedSeconds", "decisions"], "confidence": "conditional",
                           "caveat": "This does not establish that every perk would miss or that recorded requests executed."})
    if recoveries:
        inferences.append({"claim": "Health increased between some nearby samples.", "evidence": ["metrics.netHealthIncreases"],
                           "confidence": "observed net change", "caveat": "Not total healing: simultaneous damage, regeneration, and max-health changes are not separated."})
    if metrics["distanceOver20ObservedSeconds"]:
        inferences.append({"claim": "The duel included intervals beyond the service's 20-metre admission distance.",
                           "evidence": ["metrics.distanceOver20ObservedSeconds"], "confidence": "sample-based",
                           "caveat": "Admission distance is not weapon range; this does not prove wasted attacks."})
    return {"schema": 1, "sessionId": session, "complete": ended, "mode": meta.get("mode"),
            "opponentName": meta.get("opponentName"), "outcome": outcome, "endReason": end.get("reason"),
            "metrics": metrics, "packetObservations": packets, "statQualityCounts": dict(stats),
            "decisions": dict(decisions), "actionRequests": dict(actions), "equipment": equipment,
            "inferences": inferences, "limitations": warnings}


def markdown(report):
    m = report["metrics"]
    lines = [f"Duel {report['sessionId']}", f"Mode: {report['mode']}; outcome: unknown",
             f"Recording: {'ended' if report['complete'] else 'INCOMPLETE'}; samples: {m['sampleCount']}",
             f"Observed interval coverage: {m['observedIntervalSeconds']:.1f}s; gaps: {m['sampleGapSeconds']:.1f}s",
             f"Lowest sampled health: bot {m['localHealthPercentMinimum']}%; target {m['targetHealthPercentMinimum']}%",
             f"Observed Dance of Fools estimate: {m['targetDofObservedSeconds']:.1f}s",
             "", "Conditional analysis:"]
    for inference in report["inferences"]:
        lines.append("- " + inference["claim"] + " " + inference["caveat"])
    lines.extend(["", "Limitations:"] + ["- " + s for s in report["limitations"]])
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("recording", type=Path)
    parser.add_argument("--format", choices=("json", "markdown"), default="json")
    args = parser.parse_args()
    try:
        report = analyze(read_records(args.recording))
    except (ValueError, OSError) as error:
        parser.exit(2, f"Analysis failed: {error}\n")
    print(json.dumps(report, indent=2, ensure_ascii=True) if args.format == "json" else markdown(report))


if __name__ == "__main__":
    main()
