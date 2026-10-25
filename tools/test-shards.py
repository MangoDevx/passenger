#!/usr/bin/env python3
"""Which of a system's CI shards runs each test class, and the checks that every test runs in exactly one of them.

`list` writes the suite as `dotnet test --list-tests` prints it, listing again while it names no test or says discovery
failed. `split` deals the classes by their seconds on that system in TestTimes.json (else their budget), the longest
first, each to the shard with the fewest so far, so every shard of a commit deals the same. `check` fails unless every
test is in exactly one shard's settings; `cover`, after the shards, unless each succeeded and every test has exactly one
result, saying when dealing by the run's own times would end the slowest shard two minutes sooner. `update` (`just
test-times RUN`) rewrites TestTimes.json from CI runs' results.
"""
import argparse
import importlib.util
import json
import os
import shutil
import subprocess
import sys
from collections import Counter
from xml.sax.saxutils import escape

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TIMES = os.path.join(ROOT, "tests", "Passenger.Tests", "TestTimes.json")
BUDGET = os.path.join(ROOT, "tests", "Passenger.Tests", "TestBudget.json")
PROJECT = os.path.join(ROOT, "tests", "Passenger.Tests")
SYSTEMS = ("macOS", "Windows", "Linux")
# What xunit's adapter writes into the listing when it could not discover an assembly's tests.
DISCOVERY_FAILED = "Exception discovering tests"
# How much sooner the slowest shard must end, dealt from a run's own times, before the summary asks for a new deal.
WORTH_DEALING_AGAIN = 120

# test-times.py's .trx reading, loaded by its file since -I keeps this folder off the path, leaving no __pycache__.
sys.dont_write_bytecode = True
_spec = importlib.util.spec_from_file_location("test_times", os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                                                          "test-times.py"))
trx = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(trx)
PREFIX = trx.PREFIX


def say(kind, title, message):
    print(f"::{kind} title={title}::{message}" if os.environ.get("GITHUB_ACTIONS") == "true" else f"{kind}: {message}")


def summarize(lines):
    path = os.environ.get("GITHUB_STEP_SUMMARY")
    if path:
        with open(path, "a", encoding="utf-8") as file:
            file.write("\n".join(lines) + "\n\n")


def some(names):
    return ", ".join(names[:20]) + (" …" if len(names) > 20 else "")


def key(name):
    """A test's display name as both dotnet test's listing and a .trx carry it: a Windows console writes what it cannot
    encode its own way, so every character past ASCII counts as one '?'."""
    return "".join(c if c.isascii() else "?" for c in name)


def tests_in(text):
    """The tests a `dotnet test --list-tests` printout names, by display name, or None when it never says which are there."""
    lines = text.splitlines()
    starts = [i for i, line in enumerate(lines) if line.strip() == "The following Tests are available:"]
    if not starts:
        return None
    return [line.strip() for line in lines[starts[0] + 1:] if line.startswith("    ") and line.strip()]


def unlisted(text):
    """Why a `dotnet test --list-tests` printout is no listing of the suite, or None when it is one."""
    tests = tests_in(text)
    if tests is None:
        return "it never says which tests are there"
    # Discovery that failed may still have listed some of the tests, so this shard's deal would not be the others'.
    if DISCOVERY_FAILED in text:
        return f"it says \"{DISCOVERY_FAILED}\", so some or all of the tests are missing from it"
    if not tests:
        return "it names no test"
    return None


def read(path):
    with open(path, encoding="utf-8", errors="replace") as file:
        return file.read()


def listing(path):
    """The tests `dotnet test --list-tests` printed, by display name."""
    text = read(path)
    if (why := unlisted(text)) is not None:
        sys.exit(f"test-shards: {path} is not a listing of the suite: {why}. dotnet test printed:\n{text}")
    return tests_in(text)


def list_tests(arguments):
    """The suite as `dotnet test --list-tests` lists it (with a shard's settings, that shard's hand), written to a file as
    it printed it. A listing that names no test, or says discovery failed, is asked for again; still so after the last
    try, the step fails with what dotnet printed."""
    command = ["dotnet", "test", PROJECT, "--no-build", "--list-tests"]
    if arguments.settings:
        command += ["--settings", arguments.settings]
    for attempt in range(1, arguments.tries + 1):
        # Its standard error goes to the log; its output, which the listing is, as bytes, so the file is what it printed.
        done = subprocess.run(command, stdout=subprocess.PIPE, check=False)
        with open(arguments.out, "wb") as file:
            file.write(done.stdout)
        text = done.stdout.decode("utf-8", errors="replace")
        why = unlisted(text)
        if why is None:
            print(f"{len(tests_in(text))} tests listed{f' with {arguments.settings}' if arguments.settings else ''}")
            return 0
        if attempt < arguments.tries:
            say("warning", "Listing the tests", f"dotnet test --list-tests (exit code {done.returncode}) is no listing of the "
                f"suite: {why}. Listing again, try {attempt + 1} of {arguments.tries}")
            echo(done.stdout)
    say("error", "Listing the tests", f"dotnet test --list-tests is no listing of the suite in {arguments.tries} tries: "
        f"{why}. Its last exit code was {done.returncode}, and what it printed follows")
    echo(done.stdout)
    return 1


def echo(printed):
    """A program's output into the log as it printed it: Python's own encoding on Windows (cp1252) cannot hold every name."""
    sys.stdout.flush()
    sys.stdout.buffer.write(printed)
    sys.stdout.buffer.flush()


def owner(name):
    """A test's class without PREFIX, from its display name: xunit's is the class's full name, the method's and any
    arguments in brackets. A test given a display name of its own still runs with its class, whose filter goes by full
    name, but its name would be dealt too, as a class weighing a budget."""
    whole = name.split("(", 1)[0].rsplit(".", 1)[0]
    return whole[len(PREFIX):] if whole.startswith(PREFIX) else whole


def load(path, default):
    if not os.path.exists(path):
        return default
    with open(path, encoding="utf-8") as file:
        return json.load(file)


def weights(classes, system, times, budget):
    """Each class's seconds on this system, or its budget when the times have none, and the classes so weighed."""
    known = times.get(system, {})
    default = budget.get("classSeconds", 60)
    guessed = sorted(c for c in classes if c not in known)
    return {c: known.get(c, budget.get("classes", {}).get(c, default)) for c in classes}, guessed


def deal(weights, count):
    """The classes in count hands, the heaviest first, each to the lightest hand so far (the first of equals)."""
    hands = [[] for _ in range(count)]
    totals = [0.0] * count
    for name in sorted(weights, key=lambda c: (-weights[c], c)):
        lightest = min(range(count), key=lambda i: (totals[i], i))
        hands[lightest].append(name)
        totals[lightest] += weights[name]
    return hands, totals


def run_settings(classes):
    """dotnet test's settings (--settings) that run these classes' tests and no others: each test's FullyQualifiedName
    is its class's full name then its method's. A file rather than --filter, so no shell or command line holds it."""
    test_filter = "|".join(f"FullyQualifiedName~{PREFIX}{name}." for name in sorted(classes))
    return ("<RunSettings>\n  <RunConfiguration>\n    <TestCaseFilter>" + escape(test_filter)
            + "</TestCaseFilter>\n  </RunConfiguration>\n</RunSettings>\n")


def split(arguments):
    tests = listing(arguments.suite)
    classes = sorted(set(owner(test) for test in tests))
    if arguments.of < 1 or len(classes) < arguments.of:
        sys.exit(f"test-shards: {len(classes)} classes cannot be dealt to {arguments.of} shards")
    counts = Counter(owner(test) for test in tests)
    seconds, guessed = weights(classes, arguments.system, load(arguments.times, {}), load(arguments.budget, {}))
    hands, totals = deal(seconds, arguments.of)
    print(f"{len(tests)} tests in {len(classes)} classes, dealt to {arguments.of} shards by their seconds on "
          f"{arguments.system} (TestTimes.json; a class it lacks by its budget)")
    for number, (hand, total) in enumerate(zip(hands, totals), 1):
        print(f"\nShard {number}: {len(hand)} classes, {sum(counts[c] for c in hand)} tests, "
              f"about {total / 60:.1f} minutes")
        for name in hand:
            print(f"  {name:<44} {seconds[name]:>7.1f}"
                  + ("  (its budget: TestTimes.json has none)" if name in guessed else ""))
    if guessed:
        say("notice", "Test times", f"TestTimes.json has no {arguments.system} seconds for {', '.join(guessed)}, so "
            "each is dealt by its budget: just test-times RUN brings them in from a run")
    if arguments.out:
        os.makedirs(arguments.out, exist_ok=True)
        for number, hand in enumerate(hands, 1):
            with open(os.path.join(arguments.out, f"shard-{number}.runsettings"), "w", encoding="utf-8") as file:
                file.write(run_settings(hand))
    return 0


def check(arguments):
    """Every test of the suite in exactly one of the shards' listings."""
    suite = Counter(key(test) for test in listing(arguments.suite))
    listed = [Counter(key(test) for test in listing(path)) for path in arguments.listed]
    together = sum(listed, Counter())
    never = sorted((suite - together).elements())
    twice = sorted(name for name, count in together.items() if count > suite[name])
    problems = []
    if never:
        problems.append(f"{len(never)} of the suite's tests are in no shard's filter, so would never run: {some(never)}")
    if twice:
        problems.append(f"{len(twice)} tests are in more than one shard's filter (or are not the suite's), so would run "
                        f"twice: {some(twice)}")
    for problem in problems:
        say("error", "Test shards", problem)
    if not problems:
        print(f"Each of the suite's {sum(suite.values())} tests is in exactly one of the {len(listed)} shards' filters "
              f"({', '.join(str(sum(hand.values())) for hand in listed)})")
    return 1 if problems else 0


def shards(results, system):
    """One system's shards among downloaded results, by number: the folders whose shard.json says they are that shard."""
    found = {}
    for folder, _, files in sorted(os.walk(results)):
        if "shard.json" in files:
            state = load(os.path.join(folder, "shard.json"), {})
            if state.get("system") == system:
                found.setdefault(state.get("shard"), []).append((folder, state))
    return found


def cover(arguments):
    """After the shards: each ran and succeeded, and the suite's every test has exactly one result among them."""
    system = arguments.system
    found = shards(arguments.results, system)
    problems = [f"{len(places)} folders say they are shard {number} on {system}: {', '.join(f for f, _ in places)}"
                for number, places in found.items() if len(places) > 1]
    folders = {number: places[0][0] for number, places in found.items()}
    states = {number: places[0][1] for number, places in found.items()}
    counts = sorted(set(state.get("shards") for state in states.values()))
    if len(counts) > 1:
        problems.append(f"The shards on {system} disagree on how many there are: {', '.join(map(str, counts))}")
    if not found:
        problems.append(f"No shard on {system} left any results: each one's job failed or was stopped before "
                        "uploading them")
    for number in range(1, max(counts + list(folders) or [0]) + 1):
        if number not in folders:
            problems.append(f"Shard {number} on {system} left no results: its job failed or was stopped before "
                            "uploading them")
        elif states[number].get("status") != "success":
            problems.append(f"Shard {number} on {system} ended in {states[number].get('status')}: its job's log says why")

    suite = None
    for number, folder in sorted(folders.items()):
        if os.path.exists(os.path.join(folder, "suite.txt")):
            text = read(os.path.join(folder, "suite.txt"))
            if (why := unlisted(text)) is not None:
                problems.append(f"Shard {number} on {system} left no listing of the suite: {why}")
                continue
            this = Counter(key(test) for test in tests_in(text))
            if suite is None:
                suite = this
            elif this != suite:
                problems.append(f"Shard {number} on {system} listed a different suite from the others")
    ran = Counter()
    hands = {}
    for number, folder in folders.items():
        hand = hands.setdefault(number, {})
        for file in trx.trx_files([folder]):
            for test_class, name, spent, _ in trx.results(file)[1]:
                ran[key(name)] += 1
                hand[test_class] = hand.get(test_class, 0.0) + spent
    if suite is None:
        problems.append(f"No shard on {system} left its listing of the suite, so nothing says which tests should have "
                        "run")
    else:
        never = sorted((suite - ran).elements())
        twice = sorted(name for name, times in ran.items() if times > suite[name])
        if never:
            problems.append(f"{len(never)} of the suite's tests have no result from any shard on {system}: {some(never)}")
        if twice:
            problems.append(f"{len(twice)} tests have a result from more than one shard on {system}, or are not in "
                            f"the suite: {some(twice)}")
    for problem in problems:
        say("error", "Test shards", problem)

    lines = [f"### Shards on {system}", ""]
    if problems:
        lines += [f"- {problem}" for problem in problems]
    else:
        lines.append(f"Each of the suite's {sum(suite.values())} tests ran in exactly one of {len(folders)} shards.")
        print(lines[-1])
        seconds = {name: spent for hand in hands.values() for name, spent in hand.items()}
        slowest = max(sum(hand.values()) for hand in hands.values())
        _, totals = deal(seconds, len(hands))
        if slowest - max(totals) >= WORTH_DEALING_AGAIN:
            message = (f"Dealt from this run's own times, the slowest shard on {system} would take "
                       f"{max(totals) / 60:.1f} minutes of tests rather than {slowest / 60:.1f}: `just test-times "
                       f"{os.environ.get('GITHUB_RUN_ID', 'RUN')}` writes them to TestTimes.json, to commit")
            say("warning", "Test shards out of balance", message)
            lines += ["", message]
    summarize(lines)
    return 1 if problems else 0


def download(run):
    """A CI run's test results, downloaded by gh into artifacts/test-times/RUN."""
    folder = os.path.join(ROOT, "artifacts", "test-times", run)
    shutil.rmtree(folder, ignore_errors=True)
    os.makedirs(folder)
    subprocess.run(["gh", "run", "download", run, "--pattern", "test-results-*", "--dir", folder], check=True)
    return folder


def update(arguments):
    """TestTimes.json from runs' results: each class's seconds on each system, the mean of the runs that ran it."""
    old = load(arguments.times, {})
    spent = {}
    suites = {}
    for source in arguments.runs:
        folder = source if os.path.isdir(source) else download(source) if source.isdigit() else None
        if folder is None:
            sys.exit(f"test-shards: {source} is neither a folder of results nor a run's number")
        this = {}
        for file in trx.trx_files([folder]):
            system, _ = trx.shard_of(file)
            if system not in SYSTEMS:
                sys.exit(f"test-shards: nothing says which system {file} ran on: no shard.json beside it, and no "
                         "test-results-SYSTEM folder over it")
            for test_class, _, seconds, _ in trx.results(file)[1]:
                this.setdefault(system, {}).setdefault(test_class, 0.0)
                this[system][test_class] += seconds
        if not this:
            sys.exit(f"test-shards: {folder} holds no .trx results")
        for path, _, files in os.walk(folder):
            # A shard whose listing failed ran nothing, and says nothing of the suite.
            if "suite.txt" in files and unlisted(text := read(os.path.join(path, "suite.txt"))) is None:
                system, _ = trx.shard_of(os.path.join(path, "suite.txt"))
                suites.setdefault(system, set()).update(owner(test) for test in tests_in(text))
        for system, classes in this.items():
            for test_class, seconds in classes.items():
                spent.setdefault(system, {}).setdefault(test_class, []).append(seconds)
    times = {}
    for system in SYSTEMS:
        if system not in spent:
            if system in old:
                times[system] = old[system]
            continue
        measured = {name: round(sum(values) / len(values), 1) for name, values in spent[system].items()}
        # A class a shard that stopped early never reported keeps its seconds from before, while the suite has it.
        kept = {name: seconds for name, seconds in old.get(system, {}).items()
                if name not in measured and name in suites.get(system, set())}
        times[system] = dict(sorted({**kept, **measured}.items()))
        print(f"{system}: {len(measured)} classes measured" + (f", {len(kept)} kept from before" if kept else ""))
    with open(arguments.times, "w", encoding="utf-8", newline="\n") as file:
        file.write(json.dumps(times, indent=2) + "\n")
    print(f"Wrote {os.path.relpath(arguments.times, ROOT)}: commit it, and every shard deals by it")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    commands = parser.add_subparsers(dest="command", required=True)

    command = commands.add_parser("list", help="write what dotnet test --list-tests prints, listing again while it names no test")
    command.add_argument("out", help="the file the listing goes to")
    command.add_argument("--settings", help="a shard's dotnet test settings, to list only its hand")
    command.add_argument("--tries", type=int, default=3)
    command.set_defaults(run=list_tests)

    command = commands.add_parser("split", help="deal a system's test classes to its shards, their settings to --out")
    command.add_argument("suite", help="what dotnet test --list-tests printed for the whole suite")
    command.add_argument("--system", required=True, choices=SYSTEMS)
    command.add_argument("--of", type=int, required=True, help="how many shards")
    command.add_argument("--out", help="a folder for each shard's dotnet test settings, shard-N.runsettings")
    command.add_argument("--times", default=TIMES)
    command.add_argument("--budget", default=BUDGET)
    command.set_defaults(run=split)

    command = commands.add_parser("check", help="fail unless every test of the suite is in exactly one shard's listing")
    command.add_argument("suite", help="what dotnet test --list-tests printed for the whole suite")
    command.add_argument("listed", nargs="+", help="what it printed with each shard's settings")
    command.set_defaults(run=check)

    command = commands.add_parser("cover", help="fail unless every shard succeeded and every test has exactly one result")
    command.add_argument("results", help="the downloaded artifacts of the system's shards, each with its shard.json")
    command.add_argument("--system", required=True, choices=SYSTEMS)
    command.set_defaults(run=cover)

    command = commands.add_parser("update", help="rewrite TestTimes.json from CI runs' results")
    command.add_argument("runs", nargs="+", help="a CI run's number (downloaded with gh), or a folder of its artifacts")
    command.add_argument("--times", default=TIMES)
    command.set_defaults(run=update)

    arguments = parser.parse_args()
    return arguments.run(arguments)


if __name__ == "__main__":
    sys.exit(main())
