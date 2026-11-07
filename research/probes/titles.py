"""Print the title-related entries of one Claude transcript (types other than chat messages)."""
import json
import sys

path = sys.argv[1]
skip = {"user", "assistant", "attachment", "file-history-snapshot", "progress", "system"}
with open(path, encoding="utf-8") as f:
    for n, line in enumerate(f, 1):
        try:
            entry = json.loads(line)
        except ValueError:
            continue
        kind = entry.get("type")
        if kind in skip:
            continue
        short = {k: (v if not isinstance(v, (dict, list)) else "<" + type(v).__name__ + ">")
                 for k, v in entry.items() if k not in ("sessionId",)}
        for k, v in short.items():
            if isinstance(v, str) and len(v) > 90:
                short[k] = v[:90] + "..."
        print(n, kind, json.dumps(short, ensure_ascii=False))
