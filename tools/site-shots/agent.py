#!/usr/bin/env python3
# The site's agent scene (just site-shots agent): an agent trying out the Greenhouse app it is building, through
# Passenger's MCP server, each call and the answer it got written to the Terminal tab --agent runs it in. It joins the
# capture's window with passenger --mcp-attach, so every call is a real one that the window answers: it debugs the app,
# finds its button, clicks it and reads the plants' moisture back. Exits 1 on any refusal, which ends the capture
# without a picture.
import json
import os
import subprocess
import sys

BOLD, DIM, CYAN, GREEN, RED, PLAIN = "\x1b[1m", "\x1b[2m", "\x1b[36m", "\x1b[32m", "\x1b[31m", "\x1b[0m"
# The widest line the Terminal tab shows without wrapping, at the site's window size.
WIDEST = 132

server = subprocess.Popen([os.environ["PASSENGER"], "--mcp-attach", os.environ["PASSENGER_PID"]],
                          stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
asked = 0


def fail(why):
    print(f"{RED}{why}{PLAIN}", flush=True)
    sys.exit(1)


def request(method, params):
    global asked
    asked += 1
    server.stdin.write(json.dumps({"jsonrpc": "2.0", "id": asked, "method": method, "params": params}) + "\n")
    server.stdin.flush()
    while True:
        line = server.stdout.readline()
        if not line:
            fail(f"Passenger closed the connection during {method}")
        answer = json.loads(line)
        # Notifications (progress, logging) come between; the answer is the one with the request's id.
        if answer.get("id") == asked:
            if "error" in answer:
                fail(f"{method}: {answer['error'].get('message')}")
            return answer["result"]


def shown(value):
    return json.dumps(value) if not isinstance(value, str) else f'"{value}"'


def call(tool, **arguments):
    print(f"{BOLD}{CYAN}> {tool}{PLAIN} " + " ".join(f"{name}={shown(value)}" for name, value in arguments.items()), flush=True)
    result = request("tools/call", {"name": tool, "arguments": arguments})
    text = "\n".join(block["text"] for block in result.get("content", []) if block.get("type") == "text")
    if result.get("isError"):
        fail(text)
    return text


def say(text, lines=4):
    kept = [line.rstrip() for line in text.splitlines() if line.strip()]
    for line in kept[:lines]:
        print(f"  {DIM}{line if len(line) <= WIDEST else line[:WIDEST - 1] + '…'}{PLAIN}", flush=True)


hello = request("initialize", {
    "protocolVersion": "2025-06-18",
    "capabilities": {},
    "clientInfo": {"name": "greenhouse-agent", "version": "1.0"},
})
server.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
server.stdin.flush()
tools = request("tools/list", {})["tools"]
print(f"{GREEN}Connected to {hello['serverInfo'].get('title', 'Passenger')} {hello['serverInfo']['version']}: "
      f"{len(tools)} tools{PLAIN}", flush=True)

started = json.loads(call("debug_start", configuration="Greenhouse.App", waitSeconds=1))
say(f"state: {started['state']}, {started.get('status')}")
say(call("app_tree", filter="Button"))
say(call("app_click", control="#WaterButton"))
say(call("app_tree", filter="ProgressBar", properties=["Value"]))

server.stdin.close()
server.wait()
