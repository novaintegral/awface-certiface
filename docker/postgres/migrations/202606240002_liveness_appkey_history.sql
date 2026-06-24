create table if not exists awface_liveness_appkey_history (
    id uuid primary key default gen_random_uuid(),
    journey_id uuid not null references awface_liveness_journey(id),
    appkey text not null unique,
    liveness_engine varchar(3) not null,
    attempt integer not null,
    issued_at timestamptz not null default now(),
    superseded_at timestamptz,
    unique (journey_id, attempt)
);

create index if not exists ix_awface_liveness_appkey_history_journey
    on awface_liveness_appkey_history (journey_id, issued_at);

insert into awface_liveness_appkey_history (
    journey_id, appkey, liveness_engine, attempt, issued_at
)
select
    id,
    appkey,
    liveness_engine,
    1,
    coalesce(appkey_created_at, updated_at, created_at)
from awface_liveness_journey
where appkey is not null
on conflict (appkey) do nothing;
