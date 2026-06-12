CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TYPE tenant_status AS ENUM ('ACTIVE', 'BLOCKED', 'CANCELLED');
CREATE TYPE journey_type AS ENUM ('LIVENESS', 'LIVENESS_FACE_BUREAU', 'LIVENESS_FACE_BUREAU_DOCUMENT');
CREATE TYPE journey_status AS ENUM (
  'CREATED',
  'CONSENT_ACCEPTED',
  'CONSENT_REFUSED',
  'APPKEY_CREATED',
  'LIVENESS_STARTED',
  'COMPLETED',
  'FAILED'
);
CREATE TYPE consent_decision AS ENUM ('ACCEPTED', 'REFUSED');

CREATE TABLE awface_admin_user (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  email varchar(255) NOT NULL UNIQUE,
  password_hash text NOT NULL,
  active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE awface_tenant (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name varchar(180) NOT NULL,
  integration_token varchar(255) NOT NULL UNIQUE,
  status tenant_status NOT NULL DEFAULT 'ACTIVE',
  terms_url text NOT NULL,
  privacy_url text NOT NULL,
  logo_base64 text,
  callback_url text NOT NULL,
  secure_callback_token text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  cancelled_at timestamptz
);

CREATE TABLE awface_tenant_liveness_credential (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES awface_tenant(id),
  journey_type journey_type NOT NULL,
  provider_user varchar(255) NOT NULL,
  provider_pass_ciphertext text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, journey_type)
);

CREATE TABLE awface_liveness_journey (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  tenant_id uuid NOT NULL REFERENCES awface_tenant(id),
  journey_type journey_type NOT NULL,
  cpf_hash text NOT NULL,
  cpf_ciphertext text NOT NULL,
  full_name_ciphertext text NOT NULL,
  birth_date_ciphertext text NOT NULL,
  external_client_id varchar(255) NOT NULL,
  appkey text UNIQUE,
  status journey_status NOT NULL DEFAULT 'CREATED',
  user_agent text,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  completed_at timestamptz
);

CREATE INDEX awface_liveness_journey_tenant_idx ON awface_liveness_journey (tenant_id, created_at DESC);
CREATE INDEX awface_liveness_journey_external_idx ON awface_liveness_journey (tenant_id, external_client_id);
CREATE INDEX awface_liveness_journey_appkey_idx ON awface_liveness_journey (appkey);

CREATE TABLE awface_liveness_consent (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  journey_id uuid NOT NULL REFERENCES awface_liveness_journey(id),
  decision consent_decision NOT NULL,
  decided_at timestamptz NOT NULL DEFAULT now(),
  ip_address inet,
  user_agent text,
  terms_url text NOT NULL,
  privacy_url text NOT NULL
);

CREATE TABLE awface_liveness_result (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  journey_id uuid NOT NULL REFERENCES awface_liveness_journey(id),
  provider_status varchar(80) NOT NULL,
  id_externo_cliente varchar(255),
  data_criacao_appkey timestamptz,
  certiface_payload jsonb,
  facecaptcha_payload jsonb,
  photos_payload jsonb,
  raw_payload jsonb NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE awface_callback_delivery (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  journey_id uuid NOT NULL REFERENCES awface_liveness_journey(id),
  target_url text NOT NULL,
  request_payload jsonb NOT NULL,
  response_status integer,
  response_body text,
  attempt integer NOT NULL DEFAULT 1,
  delivered_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT now()
);
