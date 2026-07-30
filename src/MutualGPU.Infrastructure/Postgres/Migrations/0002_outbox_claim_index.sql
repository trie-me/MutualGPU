create index operation_outbox_claim_idx
    on operation_outbox (locked_until, available_at, id)
    where processed_at is null;
