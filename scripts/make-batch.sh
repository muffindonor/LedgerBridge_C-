#!/usr/bin/env bash
# Runs the real COBOL job on a master and transaction file, then collects
# everything LedgerBridge needs into one batch directory. Run in WSL.
#
#   scripts/make-batch.sh <dir holding ACCTMAST.DAT and TRANS.DAT> [--packed]
set -uo pipefail

REPO=$(cd "$(dirname "$0")/.." && pwd)
COBOL_HOME=${COBOL_HOME:-/mnt/d/Projects/Cobol}
BATCH=$(cd "$1" && pwd)
PACKED=${2:-}

MASTER_IN="$BATCH/ACCTMAST.DAT" TRANS_IN="$BATCH/TRANS.DAT" OUTDIR="$BATCH" NO_BUILD=1 \
    "$COBOL_HOME/run.sh" >"$BATCH/job.log" 2>&1
rc=$?
if [ "$rc" -gt 4 ]; then
    echo "COBOL job failed with return code $rc. See $BATCH/job.log"
    exit "$rc"
fi

if [ "$PACKED" = "--packed" ]; then
    BUILD=$(mktemp -d)
    trap 'rm -rf "$BUILD"' EXIT
    cobc -x -fixed -I "$REPO/copybooks" -o "$BUILD/extract" "$REPO/cobol/EXTRACT.cbl" || exit 1
    DD_ACCTIN="$BATCH/ACCTMAST.NEW" DD_ACCTPCK="$BATCH/ACCTMAST.PCK" "$BUILD/extract" || exit 1
fi

rm -f "$BATCH/job.log"
touch "$BATCH/_READY"
echo "Batch ready in $BATCH (COBOL return code $rc)"
