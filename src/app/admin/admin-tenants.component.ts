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

@Component({
  selector: 'app-admin-tenants',
  templateUrl: './admin-tenants.component.html',
  styleUrls: ['./admin.component.scss'],
})
export class AdminTenantsComponent implements OnInit {
  readonly journeyTypes = Object.entries(AWFACE_JOURNEY_LABELS).map(([value, label]) => ({
    value: value as AwfaceJourneyType,
    label,
  }));

  tenants: AwfaceTenant[] = [];
  selectedTenant: AwfaceTenant = this.createBlankTenant();
  message = '';

  constructor(
    private awfaceService: AwfaceService,
    private router: Router
  ) {}

  ngOnInit(): void {
    if (!this.awfaceService.isAdminAuthenticated()) {
      this.router.navigateByUrl('/admin');
      return;
    }

    this.loadTenants();
  }

  loadTenants(): void {
    this.awfaceService.listTenants().subscribe(tenants => {
      this.tenants = tenants;
      if (!this.selectedTenant.id && tenants.length) {
        this.selectedTenant = this.cloneTenant(tenants[0]);
      }
    });
  }

  selectTenant(tenant: AwfaceTenant): void {
    this.message = '';
    this.selectedTenant = this.cloneTenant(tenant);
  }

  newTenant(): void {
    this.message = '';
    this.selectedTenant = this.createBlankTenant();
  }

  saveTenant(): void {
    this.awfaceService.saveTenant(this.selectedTenant).subscribe(tenant => {
      this.message = `Tenant "${tenant.name}" salvo.`;
      this.selectedTenant = this.cloneTenant(tenant);
      this.loadTenants();
    });
  }

  setStatus(status: AwfaceTenantStatus): void {
    if (!this.selectedTenant.id) {
      return;
    }

    this.awfaceService.updateTenantStatus(this.selectedTenant.id, status).subscribe(tenant => {
      this.message = `Status atualizado para ${this.statusLabel(tenant.status)}.`;
      this.selectedTenant = this.cloneTenant(tenant);
      this.loadTenants();
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

  statusLabel(status: AwfaceTenantStatus): string {
    const labels: Record<AwfaceTenantStatus, string> = {
      ACTIVE: 'Ativo',
      BLOCKED: 'Bloqueado',
      CANCELLED: 'Cancelado',
    };

    return labels[status];
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
      integrationToken: '',
      status: 'ACTIVE',
      termsUrl: '',
      privacyUrl: '',
      logoBase64: '',
      callbackUrl: '',
      secureCallbackToken: '',
      credentials: [],
      createdAt: now,
      updatedAt: now,
    };
  }

  private cloneTenant(tenant: AwfaceTenant): AwfaceTenant {
    return {
      ...tenant,
      credentials: tenant.credentials.map(credential => ({ ...credential })),
    };
  }
}
