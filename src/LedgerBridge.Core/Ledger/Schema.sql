CREATE TABLE IF NOT EXISTS batch (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name              text NOT NULL,
    content_hash      bytea NOT NULL UNIQUE,
    status            text NOT NULL CHECK (status IN ('POSTING', 'RECONCILED', 'REJECTED', 'OUT_OF_BALANCE')),
    last_account_id   text,
    chunks_committed  integer NOT NULL DEFAULT 0,
    created_at        timestamptz NOT NULL DEFAULT now(),
    finished_at       timestamptz
);

CREATE TABLE IF NOT EXISTS batch_break (
    batch_id    bigint NOT NULL REFERENCES batch (id),
    seq         integer NOT NULL,
    check_name  text NOT NULL,
    detail      text NOT NULL,
    PRIMARY KEY (batch_id, seq)
);

CREATE TABLE IF NOT EXISTS account (
    account_id     text PRIMARY KEY,
    name           text NOT NULL,
    type           text NOT NULL,
    status         text NOT NULL,
    balance_cents  bigint NOT NULL,
    last_txn_date  date,
    last_batch_id  bigint NOT NULL REFERENCES batch (id)
);

-- ledger_entry, batch_account and rejected_transaction have no foreign key to batch on
-- purpose. Per-row checks more than doubled posting time (measured 28s against 12s for
-- 100,000 accounts). Each chunk also moves the batch's checkpoint in the same transaction,
-- and that fails if the batch doesn't exist, so rows for an unknown batch still can't commit.

-- One row holds both sides of the entry, so an unbalanced entry can't be stored.
-- An account's balance is its credits minus its debits.
CREATE TABLE IF NOT EXISTS ledger_entry (
    batch_id        bigint NOT NULL,
    entry_key       text NOT NULL,
    kind            text NOT NULL CHECK (kind IN ('OPEN', 'DEP', 'WDR', 'INT')),
    debit_account   text NOT NULL,
    credit_account  text NOT NULL,
    amount_cents    bigint NOT NULL CHECK (amount_cents >= 0),
    txn_date        date,
    PRIMARY KEY (batch_id, entry_key),
    CHECK (debit_account <> credit_account)
);

CREATE INDEX IF NOT EXISTS ix_ledger_entry_debit ON ledger_entry (debit_account);
CREATE INDEX IF NOT EXISTS ix_ledger_entry_credit ON ledger_entry (credit_account);

CREATE TABLE IF NOT EXISTS batch_account (
    batch_id       bigint NOT NULL,
    account_id     text NOT NULL,
    opening_cents  bigint NOT NULL,
    closing_cents  bigint NOT NULL,
    applied_count  integer NOT NULL,
    PRIMARY KEY (batch_id, account_id)
);

CREATE TABLE IF NOT EXISTS rejected_transaction (
    batch_id     bigint NOT NULL,
    txn_key      text NOT NULL,
    reason_code  text NOT NULL,
    reason_text  text NOT NULL,
    raw_record   text NOT NULL,
    PRIMARY KEY (batch_id, txn_key)
);
