"""Builds the compact test-effort file the hub's test_effort query and dashboard page read.

Runs outside the hub, by hand or on a schedule, because the hub never runs git. It only reads: the
repository's history, and an optional run retrospective. It starts nothing, builds nothing and runs
no tests. The output carries day totals and test names, never commit ids, filters, run names or paths.

    python scripts/Build-TestEffort.py --repository cocern --out <file> [--runs <retrospective.json>]

Without --runs the file still carries tests added and deleted, and the page says there is no run data.
docs/reference.md describes the input shape, the output shape and what each number means.
"""
import argparse
from collections import defaultdict
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import subprocess
import sys

SCHEMA = 1
# Mirrors RelayLimits in CoCern.Contracts; the hub refuses a file past these.
MAX_DAYS = 120
TOP_TESTS = 10
MAX_TEST_NAME = 200
TEST_NAME = re.compile(r"^[A-Za-z0-9_.`+-]+$")
TEST_ATTRIBUTE = re.compile(r"^[+-]\s*\[\s*(?:Fact|Theory)\b")
ROW_ATTRIBUTE = re.compile(r"^[+-]\s*\[\s*InlineData\b")
SOURCE_FILE = re.compile(r"^src/.+\.(?:cs|razor)$")

def git(root, *args):
    done = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, encoding="utf-8", errors="replace")
    if done.returncode != 0:
        raise RuntimeError(f"git {args[0]} failed with exit code {done.returncode}.")
    return done.stdout


def day_of(moment):
    return moment.astimezone(timezone.utc).strftime("%Y-%m-%d")


def new_day():
    return {"runs": 0, "wholeSuiteRuns": 0, "wholeSuiteMinutes": 0.0, "wallMinutes": 0.0, "caseMinutes": 0.0, "ruleAMinutes": 0.0, "ruleBMinutes": 0.0, "ruleCMinutes": 0.0,
            "pointlessEstimatedMinutes": 0.0, "failures": 0, "fixtureOnlyFailures": 0, "commits": 0, "testsAdded": 0, "testsDeleted": 0, "rowsAdded": 0, "rowsDeleted": 0}


def collect_history(root, ref, days):
    """Tests added and deleted per day, from each first-parent step of the ref. A test moved between files counts as both."""
    log = subprocess.Popen(["git", "-C", str(root), "log", ref, "--first-parent", "-m", "-U0", "--format=commit %ct", "--", "tests/*.cs"],
                           stdout=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
    current = None
    for line in log.stdout:
        if line.startswith("commit "):
            current = days[day_of(datetime.fromtimestamp(int(line.split()[1]), timezone.utc))]
            current["commits"] += 1
        elif current is None or line.startswith(("+++", "---")):
            continue
        elif TEST_ATTRIBUTE.match(line):
            current["testsAdded" if line[0] == "+" else "testsDeleted"] += 1
        elif ROW_ATTRIBUTE.match(line):
            current["rowsAdded" if line[0] == "+" else "rowsDeleted"] += 1
    if log.wait() != 0:
        raise RuntimeError("git log failed.")


def read_runs(path):
    """The documented retrospective: a list of runs, oldest order not assumed. Refuses anything else rather than guessing."""
    runs = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(runs, list):
        raise ValueError("The run file must be a JSON list of runs.")
    parsed = []
    for run in runs:
        started, ended = datetime.fromisoformat(run["StartedUtc"]), datetime.fromisoformat(run["EndedUtc"])
        if started.tzinfo is None or ended.tzinfo is None or ended < started:
            raise ValueError("Each run needs a start and an end with a time zone, the end not before the start.")
        cases = [(case["Method"], case["Outcome"], float(case["Seconds"] or 0)) for case in run["Cases"]]
        parsed.append({"commit": run.get("Commit") or None, "started": started, "wall": (ended - started).total_seconds() / 60, "whole": not run.get("Filter"), "cases": cases})
    parsed.sort(key=lambda run: run["started"])
    return parsed


def judge_rule_b(runs):
    """Rule b: every case already ran at this commit, in an earlier run. Returns the minutes of cases repeated at a commit, in any run."""
    seen = defaultdict(set)
    repeated = 0.0
    for run in runs:
        methods = {method for method, _, _ in run["cases"]}
        run["b"] = bool(run["commit"] and methods and methods <= seen[run["commit"]])
        if run["commit"]:
            repeated += sum(seconds for method, _, seconds in run["cases"] if method in seen[run["commit"]])
            seen[run["commit"]] |= methods
    return repeated / 60


def judge_rule_c(runs, root):
    """Rule c: no file under src or tests differs from the previous whole-suite run with a commit. The first has nothing to compare with."""
    previous = None
    for run in runs:
        run["c"], run["c_judged"] = False, False
        if not run["whole"] or not run["commit"]:
            continue
        if previous:
            run["c_judged"] = True
            run["c"] = run["commit"] == previous or not git(root, "diff", "--name-only", previous, run["commit"], "--", "src", "tests").strip()
        previous = run["commit"]


def judge_rule_a(runs, root):
    """Rule a, a name-matching estimate: a filtered run whose test class files name no production file its commit changed."""
    changed_by_commit, files_by_commit = {}, {}
    for run in runs:
        run["a"], run["a_judged"] = False, False
        commit = run["commit"]
        if run["whole"] or not commit or not run["cases"]:
            continue
        if commit not in changed_by_commit:
            changed = git(root, "diff", "--name-only", commit + "^1", commit, "--", "src").splitlines()
            changed_by_commit[commit] = sorted({Path(name).stem.split(".")[0] for name in changed if SOURCE_FILE.match(name)})
            files_by_commit[commit] = {Path(name).stem: name for name in git(root, "ls-tree", "-r", "--name-only", commit, "tests").splitlines() if name.endswith(".cs")}
        types, files = changed_by_commit[commit], files_by_commit[commit]
        classes = {method.split(".")[-2] for method, _, _ in run["cases"] if method.count(".") >= 1}
        paths = sorted(files[name] for name in classes if name in files)
        # Commits that change no production file, and classes with no file of their own name, give nothing to compare.
        if not types or len(paths) != len(classes):
            continue
        run["a_judged"] = True
        patterns = [item for name in types for item in ("-e", name)]
        found = subprocess.run(["git", "-C", str(root), "grep", "-l", "-w", "-F", *patterns, commit, "--", *paths], capture_output=True, text=True, encoding="utf-8", errors="replace")
        if found.returncode not in (0, 1):
            raise RuntimeError("git grep failed.")
        run["a"] = found.returncode == 1


def judge_rule_d(runs, root):
    """Rule d: failed cases that passed at the same commit, or at a later commit with no change under src. Returns (fixture-only per run, unjudged)."""
    passed = defaultdict(list)
    for order, run in enumerate(runs):
        if run["commit"]:
            for method, outcome, _ in run["cases"]:
                if outcome == "Passed":
                    passed[method].append((order, run["commit"]))
    same_source = {}

    def no_source_change(first, second):
        pair = (first, second)
        if pair not in same_source:
            same_source[pair] = first == second or not git(root, "diff", "--name-only", first, second, "--", "src").strip()
        return same_source[pair]

    unjudged = 0
    for order, run in enumerate(runs):
        run["fixture_only"] = 0
        for method, outcome, _ in run["cases"]:
            if outcome != "Failed":
                continue
            if not run["commit"]:
                unjudged += 1
            elif any(commit == run["commit"] or (later > order and no_source_change(run["commit"], commit)) for later, commit in passed[method]):
                run["fixture_only"] += 1
    return unjudged


def top_tests(runs):
    totals, failed = defaultdict(lambda: [0.0, 0]), set()
    for run in runs:
        for method, outcome, seconds in run["cases"]:
            name = method.split("(", 1)[0]
            if outcome == "Failed":
                failed.add(name)
            if outcome != "NotExecuted":
                totals[name][0] += seconds
                totals[name][1] += 1
    ranked = sorted(((name, *value) for name, value in totals.items() if name not in failed and TEST_NAME.match(name) and len(name) <= MAX_TEST_NAME), key=lambda item: (-item[1], item[0]))
    return [{"test": name, "minutes": round(seconds / 60, 2), "executions": count} for name, seconds, count in ranked[:TOP_TESTS]]


def build(root, ref, repository, runs_path):
    days = defaultdict(new_day)
    collect_history(root, ref, days)
    runs = read_runs(runs_path) if runs_path else []
    judge_rule_a(runs, root)
    repeated_case_minutes = judge_rule_b(runs)
    judge_rule_c(runs, root)
    unjudged_failures = judge_rule_d(runs, root)
    for run in runs:
        day = days[day_of(run["started"])]
        day["runs"] += 1
        day["wholeSuiteRuns"] += run["whole"]
        day["wholeSuiteMinutes"] += run["wall"] if run["whole"] else 0
        day["wallMinutes"] += run["wall"]
        day["caseMinutes"] += sum(seconds for _, _, seconds in run["cases"]) / 60
        day["ruleAMinutes"] += run["wall"] if run["a"] else 0
        day["ruleBMinutes"] += run["wall"] if run["b"] else 0
        day["ruleCMinutes"] += run["wall"] if run["c"] else 0
        day["pointlessEstimatedMinutes"] += run["wall"] if run["a"] or run["b"] or run["c"] else 0
        day["failures"] += sum(outcome == "Failed" for _, outcome, _ in run["cases"])
        day["fixtureOnlyFailures"] += run["fixture_only"]
    kept = sorted(days)[-MAX_DAYS:]
    rows = [{"day": name, **{key: round(value, 2) if isinstance(value, float) else int(value) for key, value in days[name].items()}} for name in kept]

    def rule(letter, flag, judged):
        return {"rule": letter, "runs": sum(1 for run in runs if run[flag]), "judgedRuns": judged, "minutes": round(sum(run["wall"] for run in runs if run[flag]), 2)}

    fixture_only = sum(run["fixture_only"] for run in runs)
    failures = sum(1 for run in runs for _, outcome, _ in run["cases"] if outcome == "Failed")
    return {
        "schema": SCHEMA, "repositoryId": repository, "generatedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "runs": len(runs), "cases": sum(len(run["cases"]) for run in runs), "runsWithoutCommit": sum(1 for run in runs if not run["commit"]),
        "wholeSuiteRuns": sum(1 for run in runs if run["whole"]), "wholeSuiteMinutes": round(sum(run["wall"] for run in runs if run["whole"]), 2),
        "wallMinutes": round(sum(run["wall"] for run in runs), 2), "caseMinutes": round(sum(seconds for run in runs for _, _, seconds in run["cases"]) / 60, 2),
        "repeatedCaseMinutes": round(repeated_case_minutes, 2),
        "pointlessMeasuredMinutes": round(sum(run["wall"] for run in runs if run["b"]), 2),
        "pointlessEstimatedMinutes": round(sum(run["wall"] for run in runs if run["a"] or run["b"] or run["c"]), 2),
        "failures": failures, "fixtureOnlyFailures": fixture_only, "unjudgedFailures": unjudged_failures,
        "rules": [
            rule("a", "a", sum(1 for run in runs if run["a_judged"])),
            rule("b", "b", sum(1 for run in runs if run["commit"])),
            rule("c", "c", sum(1 for run in runs if run["c_judged"])),
            {"rule": "d", "runs": sum(1 for run in runs if run["fixture_only"]), "judgedRuns": sum(1 for run in runs if run["commit"]), "minutes": None},
        ],
        "days": rows,
        "expensiveNeverFailed": top_tests(runs),
    }


def main():
    parser = argparse.ArgumentParser(description="Build the compact test-effort file for the hub.")
    parser.add_argument("--repository", required=True, help="The repository ID this file describes; the hub shows it for that repository only.")
    parser.add_argument("--out", required=True, help="The file Relay:Analytics:TestEffortFile names.")
    parser.add_argument("--runs", help="A run retrospective. Left out, the file carries git history only.")
    parser.add_argument("--root", default=".", help="The repository to read history from.")
    parser.add_argument("--ref", default="HEAD", help="The history to count, by first parent.")
    arguments = parser.parse_args()
    report = build(arguments.root, arguments.ref, arguments.repository, arguments.runs)
    # Replace in one step, so the hub never reads half a file.
    target = Path(arguments.out)
    temporary = target.with_name(target.name + ".tmp")
    temporary.write_text(json.dumps(report, separators=(",", ":")), encoding="utf-8")
    os.replace(temporary, target)
    print(f"{report['runs']} runs, {len(report['days'])} days, {target.stat().st_size} bytes.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, RuntimeError) as error:
        print(f"Build-TestEffort: {error}", file=sys.stderr)
        sys.exit(1)
