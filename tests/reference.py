"""Independent reference implementation of Azimuth's commit format, used by the tests.

    python3 tests/reference.py commit <data folder>        print the commit for a data folder
    python3 tests/reference.py check  <xbox> <player>      check an import against the Xbox source
"""
import filecmp
import hashlib
import os
import sys

XBOX_ONLY = {"TransactionLogFile"}
PROFILE = "User_9000000000000"


def commit(data):
    names = sorted(os.listdir(data))
    lines = ["Azimuth complete save revision v2", f"blobs {len(names)}"]
    for name in names:
        blob = open(os.path.join(data, name), "rb").read()
        lines.append(f"{hashlib.sha256(blob).hexdigest()} {len(blob)} {name}")
    return ("\n".join(lines) + "\n").encode()


def check(xbox, player, profile=PROFILE):
    problems = []
    for container in sorted(os.listdir(xbox)):
        base = container[: -len(",savedgame")] if container.endswith(",savedgame") else container
        target = profile if base.startswith("User_") else base
        revisions = os.path.join(player, target, "revisions")
        if not os.path.isdir(revisions) or os.listdir(revisions) != ["0000000000000001"]:
            problems.append(f"{target}: expected exactly revision 0000000000000001")
            continue
        revision = os.path.join(revisions, "0000000000000001")
        source = sorted(f for f in os.listdir(os.path.join(xbox, container)) if f not in XBOX_ONLY)
        if source != sorted(os.listdir(os.path.join(revision, "data"))):
            problems.append(f"{target}: file names differ")
            continue
        for name in source:
            if not filecmp.cmp(os.path.join(xbox, container, name), os.path.join(revision, "data", name), shallow=False):
                problems.append(f"{target}/{name}: content differs")
        if open(os.path.join(revision, "commit"), "rb").read() != commit(os.path.join(revision, "data")):
            problems.append(f"{target}: commit differs from the reference")
    return problems


if __name__ == "__main__":
    if sys.argv[1] == "commit":
        sys.stdout.buffer.write(commit(sys.argv[2]))
    elif sys.argv[1] == "check":
        found = check(sys.argv[2], sys.argv[3])
        for problem in found:
            print("  " + problem)
        sys.exit(1 if found else 0)
