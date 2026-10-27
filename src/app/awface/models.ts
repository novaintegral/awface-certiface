export type AwfaceJourneyType = 'LIVENESS' | 'LIVENESS_FACE_BUREAU' | 'LIVENESS_FACE_BUREAU_DOCUMENT';

export type AwfaceLivenessEngine = 'V9' | 'V10';

export type AwfaceTenantStatus = 'ACTIVE' | 'BLOCKED' | 'CANCELLED';

export type AwfaceTenantTheme = 'LIGHT' | 'DARK';

export type AwfaceConsentDecision = 'ACCEPTED' | 'REFUSED';

export interface AwfaceLivenessCredential {
  journeyType: AwfaceJourneyType;
  providerUser: string;
  /** Enviada somente ao alterar. A API nunca devolve a senha; vazio mantém a atual. */
  providerPass?: string;
  hasProviderPass?: boolean;
}

export interface AwfaceTenant {
  id: string;
  name: string;
  integrationToken: string;
  status: AwfaceTenantStatus;
  termsUrl: string;
  privacyUrl: string;
  logoBase64?: string;
  theme: AwfaceTenantTheme;
  primaryColor: string;
  secondaryColor: string;
  callbackUrl: string;
  secureCallbackToken: string;
  callbackOAuthEnabled: boolean;
  callbackOAuthTokenUrl?: string;
  callbackOAuthClientId?: string;
  /** Enviado somente ao alterar. A API nunca devolve o segredo; vazio mantém o atual. */
  callbackOAuthClientSecret?: string;
  hasCallbackOAuthClientSecret?: boolean;
  credentials: AwfaceLivenessCredential[];
  createdAt: string;
  updatedAt: string;
}

export interface AwfaceJourneySubject {
  cpf: string;
  fullName: string;
  birthDate: string;
  externalClientId: string;
}

export interface AwfaceJourneyStartRequest extends AwfaceJourneySubject {
  integrationToken: string;
  journeyType: AwfaceJourneyType;
}

export interface AwfaceJourneyLaunchRequest extends AwfaceJourneyStartRequest {
  hostReference?: string;
  metadata?: Record<string, unknown>;
}

export interface AwfaceJourneyLaunchResponse {
  journeyId: string;
  launchToken: string;
  launchUrl: string;
  expiresAt: string;
}

export interface AwfaceJourneyLaunchResolveResponse {
  session: AwfaceJourneySession;
}

/** Dados públicos do Tenant expostos ao usuário final (sem tokens nem credenciais). */
export interface AwfaceJourneyTenant {
  id: string;
  name: string;
  termsUrl: string;
  privacyUrl: string;
  theme: AwfaceTenantTheme;
  primaryColor: string;
  secondaryColor: string;
  logoBase64?: string;
}

export interface AwfaceJourneySession {
  id: string;
  tenant: AwfaceJourneyTenant;
  journeyType: AwfaceJourneyType;
  livenessEngine: AwfaceLivenessEngine;
  subject: Pick<AwfaceJourneySubject, 'fullName'>;
  /** Token da sessão da jornada, enviado no header X-AWFace-Journey-Token. */
  sessionToken?: string;
  sessionTokenExpiresAt?: string;
  status: 'CREATED' | 'CONSENT_ACCEPTED' | 'CONSENT_REFUSED' | 'APPKEY_CREATED' | 'LIVENESS_STARTED' | 'COMPLETED' | 'FAILED';
  appkey?: string;
  consentAt?: string;
  refusalAt?: string;
  createdAt: string;
  updatedAt: string;
}

export interface AwfaceValidationError {
  field: string;
  message: string;
}

export interface AwfaceAdminCredentials {
  email: string;
  password: string;
}

export interface AwfaceAdminLoginResponse {
  authenticated: boolean;
  accessToken: string;
  expiresAt: string;
  email: string;
}

/** DEFERRED: prova de vida concluída; a entrega ao sistema de origem segue em retentativa automática. */
export type AwfaceCompletionStatus = 'PENDING' | 'SUCCESS' | 'DEFERRED' | 'FAILED';

export interface AwfaceCompletionResult {
  status: AwfaceCompletionStatus;
  message: string;
  callbackStatus?: number;
  deliveredAt?: string;
}

export const AWFACE_JOURNEY_LABELS: Record<AwfaceJourneyType, string> = {
  LIVENESS: 'Liveness',
  LIVENESS_FACE_BUREAU: 'Liveness + Bureau de faces',
  LIVENESS_FACE_BUREAU_DOCUMENT: 'Liveness + Bureau de faces + Documentoscopia',
};
