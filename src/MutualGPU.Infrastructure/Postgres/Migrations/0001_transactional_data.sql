create type task_status as enum (
    'queued',
    'assigned',
    'running',
    'completed',
    'cancelled',
    'failed'
);

create type attempt_state as enum (
    'assigned',
    'accepted',
    'rejected',
    'disconnected',
    'revoked',
    'cancelled',
    'completed',
    'failed'
);

create type artifact_direction as enum ('input', 'output');
create type artifact_state as enum ('staged', 'available', 'orphaned', 'deleted');
create type result_upload_state as enum (
    'authorized',
    'uploading',
    'uploaded',
    'completed',
    'expired',
    'failed'
);

create table capabilities (
    id uuid primary key,
    name text not null,
    normalized_name text not null unique,
    contract_hash text not null,
    definition jsonb not null,
    created_at timestamptz not null
);

create table provider_credentials (
    digest char(64) primary key
        check (digest ~ '^[0-9a-f]{64}$'),
    execution_unit_id uuid not null unique,
    created_at timestamptz not null,
    revoked_at timestamptz null,
    version bigint not null check (version > 0)
);

create table execution_units (
    id uuid primary key,
    enrollment_version bigint not null check (enrollment_version > 0),
    persistence_version bigint not null check (persistence_version > 0),
    machine jsonb not null,
    current_enrollment jsonb not null,
    created_at timestamptz not null,
    updated_at timestamptz not null
);

create table execution_unit_capabilities (
    execution_unit_id uuid not null references execution_units(id) on delete cascade,
    capability_id uuid not null references capabilities(id),
    enrollment_version bigint not null check (enrollment_version > 0),
    primary key (execution_unit_id, capability_id)
);

create index execution_unit_capabilities_capability_idx
    on execution_unit_capabilities (capability_id, execution_unit_id);

create table enrollment_events (
    id uuid primary key default uuidv7(),
    execution_unit_id uuid not null references execution_units(id),
    enrollment_version bigint not null check (enrollment_version > 0),
    occurred_at timestamptz not null,
    snapshot jsonb not null,
    unique (execution_unit_id, enrollment_version)
);

create table tasks (
    id uuid primary key,
    requestor_id uuid not null,
    capability_id uuid not null references capabilities(id),
    capability_contract_hash text not null,
    capability_snapshot jsonb not null,
    compute_tier smallint not null check (compute_tier between 2 and 5),
    memory_gib integer not null check (memory_gib between 8 and 128),
    scalar_parameters jsonb not null,
    idempotency_key text null,
    submission_fingerprint char(64) null
        check (submission_fingerprint is null or submission_fingerprint ~ '^[0-9a-f]{64}$'),
    requestor_ip_hash text null,
    requestor_ip_class_ab text null,
    status task_status not null,
    version bigint not null check (version > 0),
    created_at timestamptz not null,
    updated_at timestamptz not null
);

create unique index tasks_requestor_idempotency_uq
    on tasks (requestor_id, idempotency_key)
    where idempotency_key is not null;

create index tasks_requestor_created_idx
    on tasks (requestor_id, created_at desc, id desc);

create index tasks_queue_idx
    on tasks (capability_id, compute_tier, memory_gib, created_at, id)
    where status = 'queued';

create table task_attempts (
    id uuid primary key,
    task_id uuid not null references tasks(id) on delete cascade,
    execution_unit_id uuid not null references execution_units(id),
    assignment_number smallint not null check (assignment_number between 1 and 4),
    handle_digest char(64) not null check (handle_digest ~ '^[0-9a-f]{64}$'),
    handle_ciphertext bytea not null,
    state attempt_state not null,
    assigned_at timestamptz not null,
    accepted_at timestamptz null,
    disconnected_at timestamptz null,
    terminal_at timestamptz null,
    failure_step text null check (failure_step is null or length(failure_step) <= 128),
    failure_reason text null check (failure_reason is null or length(failure_reason) <= 1024),
    provider_session_id uuid null,
    provider_ip_hash text null,
    provider_ip_class_ab text null,
    provider_name text null,
    provider_transport text null,
    unique (task_id, assignment_number)
);

create unique index task_attempts_one_active_uq
    on task_attempts (task_id)
    where state in ('assigned', 'accepted', 'disconnected');

create index task_attempts_execution_unit_active_idx
    on task_attempts (execution_unit_id, assigned_at)
    where state in ('assigned', 'accepted', 'disconnected');

create table task_events (
    id uuid primary key default uuidv7(),
    task_id uuid not null references tasks(id) on delete cascade,
    task_version bigint not null check (task_version > 0),
    attempt_id uuid null references task_attempts(id) on delete cascade,
    event_type text not null,
    occurred_at timestamptz not null,
    payload jsonb not null,
    unique nulls not distinct (task_id, task_version, event_type, attempt_id)
);

create index task_events_task_idx
    on task_events (task_id, occurred_at, id);

create table result_upload_operations (
    id uuid primary key default uuidv7(),
    task_id uuid not null references tasks(id) on delete cascade,
    attempt_id uuid not null references task_attempts(id) on delete cascade,
    execution_unit_id uuid not null references execution_units(id),
    handle_digest char(64) not null check (handle_digest ~ '^[0-9a-f]{64}$'),
    token_digest char(64) null unique
        check (token_digest is null or token_digest ~ '^[0-9a-f]{64}$'),
    receipt text null unique,
    state result_upload_state not null,
    expires_at timestamptz null,
    version bigint not null check (version > 0),
    created_at timestamptz not null,
    uploaded_at timestamptz null,
    completed_at timestamptz null
);

create index result_upload_attempt_idx
    on result_upload_operations (task_id, attempt_id, created_at desc);

create table artifacts (
    id uuid primary key,
    task_id uuid not null references tasks(id) on delete cascade,
    attempt_id uuid null references task_attempts(id) on delete cascade,
    result_upload_operation_id uuid null references result_upload_operations(id) on delete cascade,
    direction artifact_direction not null,
    role text not null,
    s3_object_key text not null unique,
    content_type text not null,
    length bigint not null check (length >= 0),
    sha256 char(64) not null check (sha256 ~ '^[0-9a-f]{64}$'),
    state artifact_state not null,
    created_at timestamptz not null,
    unique nulls not distinct (task_id, attempt_id, role)
);

create index artifacts_task_idx on artifacts (task_id, direction, role);
create index artifacts_upload_operation_idx
    on artifacts (result_upload_operation_id)
    where result_upload_operation_id is not null;

create table partner_resource_requests (
    id uuid primary key default uuidv7(),
    partner_name text not null,
    contact_email text not null,
    origin text not null,
    normalized_origin text not null,
    submitted_at timestamptz not null,
    approved_at timestamptz null,
    revoked_at timestamptz null,
    version bigint not null check (version > 0)
);

create index partner_resource_pending_idx
    on partner_resource_requests (submitted_at, id)
    where approved_at is null and revoked_at is null;

create index partner_resource_approved_idx
    on partner_resource_requests (normalized_origin, approved_at desc)
    where approved_at is not null and revoked_at is null;

create table operation_outbox (
    id uuid primary key default uuidv7(),
    operation_id uuid not null,
    kind text not null,
    payload jsonb not null,
    occurred_at timestamptz not null,
    available_at timestamptz not null,
    attempt_count integer not null default 0 check (attempt_count >= 0),
    processed_at timestamptz null,
    last_error_code text null,
    locked_by text null,
    locked_until timestamptz null
);

create index operation_outbox_available_idx
    on operation_outbox (available_at, occurred_at, id)
    where processed_at is null;

create table migration_ledger (
    source_store text not null,
    source_key text not null,
    source_etag text null,
    source_last_modified timestamptz null,
    destination_type text not null,
    destination_id uuid null,
    imported_at timestamptz not null,
    discrepancy_code text null,
    primary key (source_store, source_key)
);
