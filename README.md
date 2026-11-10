# LedgerBridge

A .NET 10 service that moves the output of a nightly COBOL bank batch into a modern double-entry ledger in PostgreSQL. It proves every load to the cent and survives being killed at any moment.

Banks and insurers still run their core batches on COBOL, and getting that data into modern systems safely is a large part of legacy modernization. This project works on the real output of a working COBOL job, compiled and run with GnuCOBOL.

## How it works

```
 COBOL job (BANKBTCH)                  LedgerBridge
┌──────────────────┐     ┌─────────────────────────────────────────────┐
│ ACCTMAST.DAT     │     │ 1. Identify   SHA-256 of the files          │
│ TRANS.DAT        │     │ 2. Validate   replay the whole night, check │      PostgreSQL
│ REJECTS.DAT      ├────►│               everything, post nothing      ├────► double-entry
│ ACCTMAST.NEW/.PCK│     │ 3. Post       reader ─► Channel ─► COPY     │      ledger
│ REPORT.TXT       │     │ 4. Reconcile  stored totals vs COBOL report │
└──────────────────┘     └─────────────────────────────────────────────┘
```

**Copybook-driven decoding.** The real COBOL copybooks are parsed at run time, so C# never restates a field offset. It reads text, signed display numbers, COMP-3 packed decimal and binary, using `Span<T>` with no per-record allocation.

**Replay, then prove it.** The COBOL job doesn't write down the interest each account earned, so LedgerBridge replays the night with the same balanced-line merge and recalculates it. Each account's result must match the COBOL job's new master exactly.

**Nothing is posted until everything checks out.** A full validation pass runs first, so a bad batch leaves the ledger untouched and records exactly why it failed.

**Each record lands exactly once.** A chunk of data and the checkpoint that says it's done are committed in one database transaction. After a crash, either both happened or neither did.

**Independent reconciliation.** After posting, totals are recomputed from what's actually stored and compared with the COBOL control totals.

**A double-entry ledger that can't be unbalanced.** Each row holds both the debit and the credit, so a one-sided entry can't exist.

## Measured results

Measured on a Windows desktop with PostgreSQL in Docker and normal durable commits. The test batch came from the real COBOL job: 100,000 accounts and 401,631 transactions.

| What was tested | Result |
|---|---|
| Automated tests, against real PostgreSQL | 112 of 112 pass |
| Hard kills of the worker process, across 10 full runs | 51 kills, all while posting. 10 of 10 ledgers correct to the cent |
| Same kill test on a typical loader that saves progress separately | 78 kills. 10 of 10 ledgers wrong, with 23,034 to 42,095 duplicate entries per run |
| Deliberate bugs injected to test the tests | 19 of 19 caught |
| Full run: validate, post and reconcile | 13.6 seconds, about 29,000 transactions a second |
| Decoder against a typical string-based parser, 100,000 records | 4.7 times faster. 1 MB allocated against 22 MB |

The 1 MB is the fixed read buffer, which stays the same however large the file is. The crash test reads the COBOL files with plain string slicing, so a bug in the decoder couldn't hide itself.

Compatibility with the COBOL compiler is tested directly. `golden.bin` holds 500 records written by real GnuCOBOL, covering every numeric format and its limits.

## Run it

You need Docker. This posts two real nights from the test fixtures:

```bash
mkdir -p data/inbox
cp -r tests/LedgerBridge.Tests/Fixtures/batches/day1 data/inbox/2026-11-08
cp -r tests/LedgerBridge.Tests/Fixtures/batches/day2-packed data/inbox/2026-11-09
docker compose up --build
```

Then look at the results:

```bash
curl localhost:8085/batches
curl localhost:8085/accounts/1000000002
curl "localhost:8085/accounts/1000000002/entries?limit=5"
```

Run the tests, which need Docker but not COBOL:

```bash
dotnet test tests/LedgerBridge.Tests
```

Run the benchmark:

```bash
dotnet run -c Release --project benchmarks/LedgerBridge.Benchmarks -- --filter "*"
```

## Reproduce the crash test

Start a throwaway PostgreSQL, then build a large batch with the real COBOL job. `make-batch.sh` runs in WSL and expects the BANKBTCH project at `$COBOL_HOME`.

```bash
dotnet build -c Release
dotnet tools/LedgerBridge.Tools/bin/Release/net10.0/LedgerBridge.Tools.dll generate --accounts 100000 --txns-per-account 4 --seed 99 --out .perf/big
wsl -d Ubuntu -- bash scripts/make-batch.sh .perf/big
```

```bash
dotnet tools/LedgerBridge.Tools/bin/Release/net10.0/LedgerBridge.Tools.dll chaos --batch .perf/big --worker src/LedgerBridge.Worker/bin/Release/net10.0/LedgerBridge.Worker.dll --connection "<connection string>" --rounds 10 --kills 8
```

## Batch checks

A batch is rejected, with nothing posted, if any of these fail.

| Check | What it catches |
|---|---|
| `BALANCE_MISMATCH` | Replayed balance differs from the COBOL new master |
| `*_TOTAL`, `TRANSACTIONS_*`, `ACCOUNTS_*` | Figures differ from the COBOL control totals |
| `REJECT_*` | A reject that's altered, has no transaction, or contradicts the master |
| `APPLIED_TO_UNKNOWN_ACCOUNT` | A transaction was posted to an account that doesn't exist |
| `OPENING_BALANCE_CONTINUITY` | Tonight's opening balance differs from the ledger, so a night was skipped or repeated |
| `ACCOUNT_MISSING_FROM_MASTER` | An account in the ledger has vanished from the master |
| `SEQUENCE`, `RECORD_FORMAT`, `MISSING_FILE` | Unusable files: out of order, corrupt or incomplete |

## Project layout

| Path | Contents |
|---|---|
| `src/LedgerBridge.Cobol` | Copybook parser, numeric decoders, record reader. No dependencies |
| `src/LedgerBridge.Core` | Replay engine, validation, posting pipeline, reconciliation |
| `src/LedgerBridge.Worker` | Watches an inbox folder and processes batches in order |
| `src/LedgerBridge.Api` | Read-only API over the ledger |
| `tools/LedgerBridge.Tools` | Batch generator, crash test, naive control |
| `cobol/`, `copybooks/` | COBOL used to make test data, and the record layouts |

## Limits

- It hasn't read a file from a real mainframe. Real extracts are usually EBCDIC, which isn't supported yet.
- The kill test kills the worker process. It doesn't simulate a power cut on the database server.
- A batch that goes out of balance after posting stops later nights and needs a person to investigate. That has only been triggered by deliberately corrupting data in a test.
- `REDEFINES` and `OCCURS` in copybooks are refused with a clear error rather than handled.
- The API has no authentication. It's bound to localhost in Docker and must not be exposed as it is.
- Reconciliation sums the whole ledger each night, so that step will slow down as years of history build up.
