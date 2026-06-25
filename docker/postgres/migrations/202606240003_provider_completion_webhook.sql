create table if not exists awface_liveness_submission (
    appkey text primary key,
    journey_id uuid not null references awface_liveness_journey(id),
    liveness_engine varchar(3) not null,
    provider_response jsonb not null,
    device_location jsonb,
    submitted_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create index if not exists ix_awface_liveness_submission_journey
    on awface_liveness_submission (journey_id, submitted_at);

create table if not exists awface_provider_notification (
    id uuid primary key default gen_random_uuid(),
    journey_id uuid not null references awface_liveness_journey(id),
    appkey text not null,
    provider_status varchar(80) not null,
    attempts integer not null default 1,
    processing_started_at timestamptz,
    processed_at timestamptz,
    last_error text,
    received_at timestamptz not null default now(),
    unique (appkey)
);

create unique index if not exists ux_awface_provider_notification_appkey
    on awface_provider_notification (appkey);
