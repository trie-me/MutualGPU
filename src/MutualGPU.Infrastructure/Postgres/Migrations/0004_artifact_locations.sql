create type artifact_location_state as enum (
    'staged',
    'available',
    'orphaned',
    'deleted',
    'failed'
);

alter table result_upload_operations
    add column write_storage_target_id text not null default 'aws-primary'
        check (length(btrim(write_storage_target_id)) > 0);

create table artifact_locations (
    artifact_id uuid not null references artifacts(id) on delete cascade,
    storage_target_id text not null
        check (length(btrim(storage_target_id)) > 0),
    object_key text not null
        check (length(object_key) > 0),
    provider_etag text null,
    state artifact_location_state not null,
    created_at timestamptz not null,
    last_verified_at timestamptz null,
    primary key (artifact_id, storage_target_id),
    unique (storage_target_id, object_key)
);

create index artifact_locations_target_state_idx
    on artifact_locations (storage_target_id, state, artifact_id);

insert into artifact_locations(
    artifact_id, storage_target_id, object_key, provider_etag,
    state, created_at, last_verified_at)
select
    id, 'aws-primary', s3_object_key, null,
    state::text::artifact_location_state, created_at, null
from artifacts;
