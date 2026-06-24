export type AwfaceJourneyType = 'LIVENESS' | 'LIVENESS_FACE_BUREAU' | 'LIVENESS_FACE_BUREAU_DOCUMENT';

export type AwfaceLivenessEngine = 'V9' | 'V10';

export type AwfaceTenantStatus = 'ACTIVE' | 'BLOCKED' | 'CANCELLED';

export type AwfaceTenantTheme = 'LIGHT' | 'DARK';

export type AwfaceConsentDecision = 'ACCEPTED' | 'REFUSED';

export interface AwfaceLivenessCredential {
  journeyType: AwfaceJourneyType;
  providerUser: string;
  providerPass: string;
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
  callbackOAuthClientSecret?: string;
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

export interface AwfaceJourneySession {
  id: string;
  tenant: AwfaceTenant;
  journeyType: AwfaceJourneyType;
  livenessEngine: AwfaceLivenessEngine;
  subject: AwfaceJourneySubject;
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

export type AwfaceCompletionStatus = 'PENDING' | 'SUCCESS' | 'FAILED';

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
