alter table awface_liveness_journey
    add column if not exists liveness_engine varchar(3) not null default 'V10';

update awface_liveness_journey
set liveness_engine = 'V10'
where liveness_engine is null or liveness_engine not in ('V9', 'V10');
