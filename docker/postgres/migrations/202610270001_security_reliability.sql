-- AWFace 1.1.0: administradores, outbox de webhooks com retentativa e deduplicação de requisições do SDK.
-- Aplicada automaticamente pela AWFace.Api (AwfaceSchemaInitializer, id 202610270001_security_reliability).

create table if not exists awface_admin_user (
    id uuid primary key default gen_random_uuid(),
    email varchar(255) not null unique,
    password_hash text not null,
    active boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create table if not exists awface_callback_outbox (
    id uuid primary key,
    journey_id uuid not null references awface_liveness_journey(id),
    event_key text not null unique,
    payload jsonb not null,
    status varchar(16) not null default 'PENDING'
        check (status in ('PENDING', 'SENDING', 'DELIVERED', 'FAILED')),
    attempts integer not null default 0,
    next_attempt_at timestamptz not null default now(),
    locked_until timestamptz,
    last_response_status integer,
    last_error text,
    delivered_at timestamptz,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create index if not exists ix_awface_callback_outbox_due
    on awface_callback_outbox (status, next_attempt_at);

create index if not exists ix_awface_callback_outbox_journey
    on awface_callback_outbox (journey_id, created_at desc);

alter table awface_callback_delivery
    add column if not exists outbox_id uuid references awface_callback_outbox(id);

create table if not exists awface_sdk_request (
    request_key text primary key,
    journey_id uuid not null references awface_liveness_journey(id),
    appkey text not null,
    operation varchar(40) not null,
    status varchar(16) not null check (status in ('PROCESSING', 'COMPLETED')),
    response_status integer,
    response_content_type text,
    response_body text,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now()
);

create index if not exists ix_awface_sdk_request_updated_at
    on awface_sdk_request (updated_at);
