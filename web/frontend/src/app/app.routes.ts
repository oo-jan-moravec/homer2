import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', redirectTo: 'drive', pathMatch: 'full' },
  { path: 'drive', loadComponent: () => import('./pages/operator-page/operator-page.component').then(m => m.OperatorPageComponent) },
  { path: 'console', loadComponent: () => import('./pages/console-page/console-page.component').then(m => m.ConsolePageComponent) },
];
