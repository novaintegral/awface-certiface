import { Component, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { AwfaceService } from '../awface/awface.service';
import {
  AWFACE_JOURNEY_LABELS,
  AwfaceJourneyType,
  AwfaceLivenessCredential,
  AwfaceTenant,
  AwfaceTenantStatus,
} from '../awface/models';
import { AWFACE_VERSION } from '../core/release-version';

@Component({
  selector: 'app-admin-tenants',
  templateUrl: './admin-tenants.component.html',
  styleUrls: ['./admin.component.scss'],
})
export class AdminTenantsComponent implements OnInit {
  readonly awfaceVersion = AWFACE_VERSION;
  private readonly maxLogoSizeBytes = 256 * 1024;

  readonly journeyTypes = Object.entries(AWFACE_JOURNEY_LABELS).map(([value, label]) => ({
    value: value as AwfaceJourneyType,
    label,
  }));

  tenants: AwfaceTenant[] = [];
  selectedTenant: AwfaceTenant = this.createBlankTenant();
  message = '';
  activeTab: 'identity' | 'appearance' | 'webhook' | 'oauth' | 'credentials' = 'identity';

  constructor(
    private awfaceService: AwfaceService,
    private router: Router
  ) {}

  ngOnInit(): void {
    this.loadTenants();
  }

  loadTenants(): void {
    this.awfaceService.listTenants().subscribe({
      next: tenants => {
        this.tenants = tenants;
        if (!this.selectedTenant.id && tenants.length) {
          this.selectedTenant = this.cloneTenant(tenants[0]);
        }
      },
      error: error => this.message = this.describeError(error, 'Não foi possível carregar os tenants.'),
    });
  }

  selectTenant(tenant: AwfaceTenant): void {
    this.message = '';
    this.selectedTenant = this.cloneTenant(tenant);
    this.activeTab = 'identity';
  }

  newTenant(): void {
    this.message = '';
    this.selectedTenant = this.createBlankTenant();
    this.activeTab = 'identity';
  }

  saveTenant(): void {
    this.awfaceService.saveTenant(this.selectedTenant).subscribe({
      next: tenant => {
        this.message = `Tenant "${tenant.name}" salvo.`;
        this.selectedTenant = this.cloneTenant(tenant);
        this.loadTenants();
      },
      error: error => this.message = this.describeError(error, 'Não foi possível salvar o tenant.'),
    });
  }

  setStatus(status: AwfaceTenantStatus): void {
    if (!this.selectedTenant.id) {
      return;
    }

    this.awfaceService.updateTenantStatus(this.selectedTenant.id, status).subscribe({
      next: tenant => {
        this.message = `Status atualizado para ${this.statusLabel(tenant.status)}.`;
        this.selectedTenant = this.cloneTenant(tenant);
        this.loadTenants();
      },
      error: error => this.message = this.describeError(error, 'Não foi possível atualizar o status.'),
    });
  }

  hasCredential(journeyType: AwfaceJourneyType): boolean {
    return this.selectedTenant.credentials.some(credential => credential.journeyType === journeyType);
  }

  toggleCredential(journeyType: AwfaceJourneyType, checked: boolean): void {
    if (checked) {
      this.selectedTenant.credentials = [
        ...this.selectedTenant.credentials,
        { journeyType, providerUser: '', providerPass: '' },
      ];
      return;
    }

    this.selectedTenant.credentials = this.selectedTenant.credentials.filter(
      credential => credential.journeyType !== journeyType
    );
  }

  getCredential(journeyType: AwfaceJourneyType): AwfaceLivenessCredential {
    let credential = this.selectedTenant.credentials.find(item => item.journeyType === journeyType);

    if (!credential) {
      credential = { journeyType, providerUser: '', providerPass: '' };
      this.selectedTenant.credentials = [...this.selectedTenant.credentials, credential];
    }

    return credential;
  }

  onLogoFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) {
      return;
    }

    if (!file.type.startsWith('image/')) {
      this.message = 'Selecione um arquivo de imagem para o logotipo.';
      input.value = '';
      return;
    }

    if (file.size > this.maxLogoSizeBytes) {
      this.message = 'O logotipo deve ter no máximo 256 KB. Use uma imagem otimizada para web.';
      input.value = '';
      return;
    }

    const reader = new FileReader();
    reader.onload = () => {
      this.selectedTenant.logoBase64 = String(reader.result || '');
      this.message = 'Logotipo carregado. Salve o tenant para persistir a alteração.';
      input.value = '';
    };
    reader.readAsDataURL(file);
  }

  getLogoPreview(): string {
    return this.awfaceService.getLogoSource(this.selectedTenant.logoBase64);
  }

  regenerateSecureCallbackToken(): void {
    this.selectedTenant.secureCallbackToken = this.createSecret();
    this.message = 'Segredo do webhook regenerado. Salve o tenant e atualize a aplicação Host.';
  }

  regenerateIntegrationToken(): void {
    this.selectedTenant.integrationToken = this.createIntegrationToken();
    this.message = 'Token de integração regenerado. Salve o tenant para persistir a alteração.';
  }

  copyIntegrationToken(token = this.selectedTenant.integrationToken): void {
    token = token?.trim();
    if (!token) {
      this.message = 'Não há token de integração para copiar.';
      return;
    }

    if (navigator.clipboard?.writeText) {
      navigator.clipboard.writeText(token)
        .then(() => this.message = 'Token de integração copiado para a área de transferência.')
        .catch(() => this.copyIntegrationTokenFallback(token));
      return;
    }

    this.copyIntegrationTokenFallback(token);
  }

  statusLabel(status: AwfaceTenantStatus): string {
    const labels: Record<AwfaceTenantStatus, string> = {
      ACTIVE: 'Ativo',
      BLOCKED: 'Bloqueado',
      CANCELLED: 'Cancelado',
    };

    return labels[status];
  }

  statusClass(status: AwfaceTenantStatus): string {
    const classes: Record<AwfaceTenantStatus, string> = {
      ACTIVE: 'text-bg-success',
      BLOCKED: 'text-bg-warning',
      CANCELLED: 'text-bg-danger',
    };

    return classes[status];
  }

  logout(): void {
    this.awfaceService.logoutAdmin();
    this.router.navigateByUrl('/admin');
  }

  private createBlankTenant(): AwfaceTenant {
    const now = new Date().toISOString();
    return {
      id: '',
      name: '',
      integrationToken: this.createIntegrationToken(),
      status: 'ACTIVE',
      termsUrl: '',
      privacyUrl: '',
      logoBase64: '',
      theme: 'LIGHT',
      primaryColor: '#007060',
      secondaryColor: '#315f88',
      callbackUrl: '',
      secureCallbackToken: this.createSecret(),
      callbackOAuthEnabled: false,
      callbackOAuthTokenUrl: '',
      callbackOAuthClientId: '',
      callbackOAuthClientSecret: '',
      credentials: [],
      createdAt: now,
      updatedAt: now,
    };
  }

  private createIntegrationToken(): string {
    return `awf_${crypto.randomUUID().replace(/-/g, '')}`;
  }

  private createSecret(): string {
    return `${crypto.randomUUID()}${crypto.randomUUID()}`.replace(/-/g, '');
  }

  private describeError(error: any, fallback: string): string {
    const body = error?.error;
    if (Array.isArray(body) && body.length) {
      return body.map((item: { message?: string }) => item.message).filter(Boolean).join(' ');
    }

    return body?.detail || body?.message || fallback;
  }

  private copyIntegrationTokenFallback(token: string): void {
    const textarea = document.createElement('textarea');
    textarea.value = token;
    textarea.setAttribute('readonly', 'true');
    textarea.style.position = 'fixed';
    textarea.style.opacity = '0';
    document.body.appendChild(textarea);
    textarea.select();

    const copied = document.execCommand('copy');
    document.body.removeChild(textarea);
    this.message = copied
      ? 'Token de integração copiado para a área de transferência.'
      : 'Não foi possível copiar o token automaticamente.';
  }

  private cloneTenant(tenant: AwfaceTenant): AwfaceTenant {
    return {
      ...tenant,
      callbackOAuthClientSecret: '',
      credentials: tenant.credentials.map(credential => ({ ...credential, providerPass: '' })),
    };
  }
}
