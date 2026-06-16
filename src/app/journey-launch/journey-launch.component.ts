import { Component, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { AwfaceService } from '../awface/awface.service';
import { AwfaceJourneySession } from '../awface/models';

@Component({
  selector: 'app-journey-launch',
  templateUrl: './journey-launch.component.html',
  styleUrls: ['./journey-launch.component.scss'],
})
export class JourneyLaunchComponent implements OnInit {
  session?: AwfaceJourneySession;
  status: 'loading' | 'consent' | 'refused' | 'failed' = 'loading';
  title = 'Preparando sua prova de vida';
  message = 'Estamos validando o acesso enviado pela aplicação de origem.';

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    public awfaceService: AwfaceService
  ) {}

  ngOnInit(): void {
    const launchToken = this.route.snapshot.queryParamMap.get('token');

    if (!launchToken) {
      this.showFailure('Link inválido', 'O token de lançamento não foi informado. Solicite um novo acesso.');
      return;
    }

    this.awfaceService.consumeJourneyLaunch(launchToken).subscribe({
      next: session => {
        this.session = session;
        this.status = 'consent';
        this.title = 'Você autoriza o uso da sua biometria facial?';
        this.message = 'Precisamos da sua permissão para iniciar a prova de vida.';
      },
      error: error => this.showFailure(
        'Não foi possível iniciar a prova de vida',
        error?.error?.message || 'O link está expirado ou não pode mais ser utilizado. Solicite um novo acesso.'
      ),
    });
  }

  acceptConsent(): void {
    if (!this.session) {
      return;
    }

    this.status = 'loading';
    this.title = 'Preparando sua prova de vida';
    this.message = 'Registrando sua autorização e abrindo a validação facial.';

    this.awfaceService.registerConsent(this.session.id, 'ACCEPTED').subscribe({
      next: session => {
        this.session = session;
        this.awfaceService.issueAppkey(session).subscribe({
          next: () => this.router.navigateByUrl('/journey'),
          error: error => this.showFailure(
            'Não foi possível iniciar a prova de vida',
            error?.error?.message || 'Não foi possível gerar a chave de acesso para a validação facial.'
          ),
        });
      },
      error: () => this.showFailure(
        'Não foi possível registrar sua autorização',
        'Tente novamente ou entre em contato com o administrador do sistema.'
      ),
    });
  }

  refuseConsent(): void {
    if (!this.session) {
      return;
    }

    this.status = 'loading';
    this.title = 'Registrando sua escolha';
    this.message = 'Aguarde um instante.';

    this.awfaceService.registerConsent(this.session.id, 'REFUSED').subscribe({
      next: session => {
        this.session = session;
        this.status = 'refused';
        this.title = 'Autorização não concedida';
        this.message = 'Sem a autorização, não podemos iniciar a prova de vida. Sua escolha foi registrada.';
      },
      error: () => this.showFailure(
        'Não foi possível registrar sua escolha',
        'Tente novamente ou entre em contato com o administrador do sistema.'
      ),
    });
  }

  getTenantLogo(): string {
    return this.awfaceService.getLogoSource(this.session?.tenant.logoBase64);
  }

  private showFailure(title: string, message: string): void {
    this.status = 'failed';
    this.title = title;
    this.message = message;
  }
}
