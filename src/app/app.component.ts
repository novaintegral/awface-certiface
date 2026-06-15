import { Component, VERSION } from '@angular/core';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.scss']
})
export class AppComponent {
  title = 'Validação de Prova de Vida';

  ngOnInit(): void {
    console.log("Angular version: " + VERSION.full);
  }
}
