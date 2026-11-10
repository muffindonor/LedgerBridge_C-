#!/usr/bin/env bash
# Regenerates the test fixtures with real GnuCOBOL, so the C# tests can run without it.
# Run in WSL. Needs the BANKBTCH project at $COBOL_HOME.
# The medium batch inputs come from: dotnet run --project tools/LedgerBridge.Tools -- generate
set -euo pipefail

REPO=$(cd "$(dirname "$0")/.." && pwd)
COBOL_HOME=${COBOL_HOME:-/mnt/d/Projects/Cobol}
FIXTURES=$REPO/tests/LedgerBridge.Tests/Fixtures
BUILD=$(mktemp -d)
trap 'rm -rf "$BUILD"' EXIT

#--- Golden numeric formats ---------------------------------------------------
cd "$FIXTURES/golden"
python3 - <<'PY'
import random
random.seed(20261109)
def signed(n, value):
    return ('-' if value < 0 else '+') + str(abs(value)).zfill(n)
def row(case, odd, even, uns, mx, disp, b4, b9, b18):
    return f"{case:04d}{signed(13, odd)}{signed(12, even)}{uns:05d}{signed(18, mx)}{signed(7, disp)}{signed(4, b4)}{signed(9, b9)}{signed(18, b18)}"
def r(n):
    limit = 10 ** n - 1
    return random.randint(-limit, limit)
m18, m13, m12, m9, m7, m4 = 10**18-1, 10**13-1, 10**12-1, 10**9-1, 10**7-1, 10**4-1
rows = [
    row(1, 0, 0, 0, 0, 0, 0, 0, 0),
    row(2, 1, 1, 1, 1, 1, 1, 1, 1),
    row(3, -1, -1, 1, -1, -1, -1, -1, -1),
    row(4, m13, m12, 99999, m18, m7, m4, m9, m18),
    row(5, -m13, -m12, 99999, -m18, -m7, -m4, -m9, -m18),
    row(6, 10, 10, 10, 10, 10, 10, 10, 10),
    row(7, -100, -100, 100, -100, -100, -100, -100, -100),
]
# Every last digit, positive and negative, exercises the whole overpunch table.
for d in range(10):
    rows.append(row(8 + d, d, d, d, d, d, d, d, d))
    rows.append(row(18 + d, -d, -d, d, -d, -d, -d, -d, -d))
for case in range(28, 501):
    rows.append(row(case, r(13), r(12), random.randint(0, 99999), r(18), r(7), r(4), r(9), r(18)))
open("golden.txt", "w", newline="\n").write("\n".join(rows) + "\n")
PY

cobc -x -fixed -I . -o "$BUILD/golden" "$REPO/cobol/GOLDEN.cbl"
DD_GOLDIN=golden.txt DD_GOLDOUT=golden.bin "$BUILD/golden"
echo "golden: $(wc -l <golden.txt) rows, $(wc -c <golden.bin) bytes"

#--- Batches through the real COBOL job ---------------------------------------
cd "$FIXTURES/batches"
rm -rf day1 day2 day2-packed
mkdir day1 day2 day2-packed

cp "$COBOL_HOME/data/ACCTMAST.DAT" "$COBOL_HOME/data/TRANS.DAT" day1/
bash "$REPO/scripts/make-batch.sh" day1

cp day1/ACCTMAST.NEW day2/ACCTMAST.DAT
cat >day2/TRANS.DAT <<'DATA'
1000000001000001DEP0000001000020261109
1000000002000001INT0000000000020261109
1000000003000001WDR0000000050020261109
1000000003000002WDR0000999999920261109
1000000005000001INT0000000000020261109
1000000099000001DEP0000000010020261109
DATA
bash "$REPO/scripts/make-batch.sh" day2

cp day2/ACCTMAST.DAT day2/TRANS.DAT day2-packed/
bash "$REPO/scripts/make-batch.sh" day2-packed --packed

bash "$REPO/scripts/make-batch.sh" medium
