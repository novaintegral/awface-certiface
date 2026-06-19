import { Component, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { AwfaceService } from '../awface/awface.service';
import {
  AWFACE_JOURNEY_LABELS,
  AwfaceJourneySession,
  AwfaceJourneyStartRequest,
  AwfaceJourneyType,
  AwfaceValidationError,
} from '../awface/models';
import { validateJourneyStart } from '../awface/awface-validators';

@Component({
  selector: 'app-journey',
  templateUrl: './journey.component.html',
  styleUrls: ['./journey.component.scss'],
})
export class JourneyComponent implements OnInit {
  readonly journeyTypes = Object.entries(AWFACE_JOURNEY_LABELS).map(([value, label]) => ({
    value: value as AwfaceJourneyType,
    label,
  }));

  step: 'identify' | 'consent' | 'refused' | 'loading' = 'identify';
  session?: AwfaceJourneySession;
  form: AwfaceJourneyStartRequest = {
    integrationToken: '',
    journeyType: 'LIVENESS',
    cpf: '',
    fullName: '',
    birthDate: '',
    externalClientId: '',
  };
  errors: Record<string, string> = {};
  generalError = '';
  loadingMessage = '';

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    public awfaceService: AwfaceService
  ) {}

  ngOnInit(): void {
    this.route.queryParamMap.subscribe(params => {
      this.form = {
        integrationToken: params.get('token') || params.get('integrationToken') || this.form.integrationToken,
        journeyType: (params.get('journeyType') as AwfaceJourneyType) || this.form.journeyType,
        cpf: params.get('cpf') || this.form.cpf,
        fullName: params.get('nome') || params.get('fullName') || this.form.fullName,
        birthDate: this.normalizeBirthDate(params.get('nascimento') || params.get('birthDate') || this.form.birthDate),
        externalClientId: params.get('idExternoCliente') || params.get('externalClientId') || this.form.externalClientId,
      };
    });
  }

  startJourney(): void {
    this.generalError = '';
    this.errors = this.toErrorMap(validateJourneyStart(this.form));

    if (Object.keys(this.errors).length) {
      return;
    }

    this.step = 'loading';
    this.loadingMessage = 'Preparando sua validação com segurança...';

    this.awfaceService.startJourney(this.form).subscribe({
      next: session => {
        this.session = session;
        this.step = 'consent';
      },
      error: error => {
        this.step = 'identify';
        this.applyError(error);
      },
    });
  }

  acceptConsent(): void {
    if (!this.session) {
      return;
    }

    this.step = 'loading';
    this.loadingMessage = 'Registrando sua autorização...';

    this.awfaceService.registerConsent(this.session.id, 'ACCEPTED').subscribe({
      next: session => {
        this.session = session;
        this.loadingMessage = 'Abrindo a prova de vida...';
        this.issueAppkeyAndContinue(session);
      },
      error: () => {
        this.step = 'consent';
        this.generalError = 'Não foi possível registrar sua autorização. Tente novamente.';
      },
    });
  }

  refuseConsent(): void {
    if (!this.session) {
      return;
    }

    this.step = 'loading';
    this.loadingMessage = 'Registrando sua escolha...';

    this.awfaceService.registerConsent(this.session.id, 'REFUSED').subscribe({
      next: session => {
        this.session = session;
        this.step = 'refused';
      },
      error: () => {
        this.step = 'consent';
        this.generalError = 'Não foi possível registrar sua recusa. Tente novamente.';
      },
    });
  }

  getTenantLogo(): string {
    return this.awfaceService.getJourneyLogoSource(this.session);
  }

  private issueAppkeyAndContinue(session: AwfaceJourneySession): void {
    this.awfaceService.issueAppkey(session).subscribe({
      next: () => this.router.navigateByUrl('/journey'),
      error: error => {
        this.step = 'consent';
        const operation = error?.error?.providerOperation;
        this.generalError = operation
          ? `Não foi possível iniciar a prova de vida agora. Falha na etapa Certiface: ${operation}.`
          : 'Não foi possível iniciar a prova de vida agora. Verifique a credencial do tenant.';
      },
    });
  }

  private toErrorMap(errors: AwfaceValidationError[]): Record<string, string> {
    return errors.reduce((acc, error) => ({ ...acc, [error.field]: error.message }), {});
  }

  private applyError(error: unknown): void {
    if (Array.isArray(error)) {
      this.errors = this.toErrorMap(error as AwfaceValidationError[]);
      return;
    }

    this.generalError = 'Não foi possível iniciar a jornada. Verifique os dados e tente novamente.';
  }

  private normalizeBirthDate(value: string): string {
    if (/^\d{2}\/\d{2}\/\d{4}$/.test(value)) {
      const [day, month, year] = value.split('/');
      return `${year}-${month}-${day}`;
    }

    return value;
  }
}
