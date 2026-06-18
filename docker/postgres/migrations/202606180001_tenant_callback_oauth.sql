CREATE TABLE IF NOT EXISTS awface_schema_migration (
  id text PRIMARY KEY,
  description text NOT NULL,
  applied_at timestamptz NOT NULL DEFAULT now()
);

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM awface_schema_migration WHERE id = '202606180001_tenant_callback_oauth') THEN
    ALTER TABLE awface_tenant
      ADD COLUMN IF NOT EXISTS callback_oauth_enabled boolean NOT NULL DEFAULT false,
      ADD COLUMN IF NOT EXISTS callback_oauth_token_url text,
      ADD COLUMN IF NOT EXISTS callback_oauth_client_id text,
      ADD COLUMN IF NOT EXISTS callback_oauth_client_secret_ciphertext text;

    INSERT INTO awface_schema_migration (id, description)
    VALUES ('202606180001_tenant_callback_oauth', 'Adiciona autenticação OAuth2 ao webhook do tenant.');
  END IF;
END $$;
