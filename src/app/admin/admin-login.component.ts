import { Component } from '@angular/core';
import { Router } from '@angular/router';
import { AwfaceService } from '../awface/awface.service';

@Component({
  selector: 'app-admin-login',
  templateUrl: './admin-login.component.html',
  styleUrls: ['./admin.component.scss'],
})
export class AdminLoginComponent {
  email = '';
  password = '';
  error = '';

  constructor(private awfaceService: AwfaceService, private router: Router) {}

  login(): void {
    this.error = '';

    if (this.awfaceService.loginAdmin({ email: this.email, password: this.password })) {
      this.router.navigateByUrl('/admin/tenants');
      return;
    }

    this.error = 'Login ou senha inválidos.';
  }
}
