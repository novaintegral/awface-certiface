import { AwfaceJourneyStartRequest, AwfaceValidationError } from './models';

export function onlyDigits(value: string): string {
  return (value || '').replace(/\D/g, '');
}

export function isValidCpf(cpf: string): boolean {
  const normalized = onlyDigits(cpf);

  if (normalized.length !== 11 || /^(\d)\1{10}$/.test(normalized)) {
    return false;
  }

  const calcDigit = (base: string, factor: number): number => {
    let total = 0;
    for (const digit of base) {
      total += Number(digit) * factor;
      factor -= 1;
    }
    const rest = (total * 10) % 11;
    return rest === 10 ? 0 : rest;
  };

  const firstDigit = calcDigit(normalized.slice(0, 9), 10);
  const secondDigit = calcDigit(normalized.slice(0, 10), 11);

  return firstDigit === Number(normalized[9]) && secondDigit === Number(normalized[10]);
}

export function isCompleteName(fullName: string): boolean {
  const parts = (fullName || '')
    .trim()
    .split(/\s+/)
    .filter(Boolean);

  return parts.length >= 2 && parts.every(part => part.length >= 2);
}

export function isValidBirthDate(value: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value || '')) {
    return false;
  }

  const date = new Date(`${value}T00:00:00`);
  const now = new Date();
  const minDate = new Date(now.getFullYear() - 120, now.getMonth(), now.getDate());

  return !Number.isNaN(date.getTime()) && date < now && date > minDate;
}

export function validateJourneyStart(request: AwfaceJourneyStartRequest): AwfaceValidationError[] {
  const errors: AwfaceValidationError[] = [];

  if (!request.integrationToken?.trim()) {
    errors.push({ field: 'integrationToken', message: 'Informe o token de integração.' });
  }

  if (!request.journeyType) {
    errors.push({ field: 'journeyType', message: 'Selecione o tipo de jornada.' });
  }

  if (!isValidCpf(request.cpf)) {
    errors.push({ field: 'cpf', message: 'Informe um CPF válido.' });
  }

  if (!isCompleteName(request.fullName)) {
    errors.push({ field: 'fullName', message: 'Informe nome e sobrenome.' });
  }

  if (!isValidBirthDate(request.birthDate)) {
    errors.push({ field: 'birthDate', message: 'Informe uma data de nascimento válida.' });
  }

  if (!request.externalClientId?.trim()) {
    errors.push({ field: 'externalClientId', message: 'Informe a referência externa da jornada.' });
  }

  if ((request.externalClientId || '').length > 255) {
    errors.push({ field: 'externalClientId', message: 'A referência externa deve ter até 255 caracteres.' });
  }

  if (!/^[a-zA-Z0-9_.-]+$/.test(request.externalClientId || '')) {
    errors.push({
      field: 'externalClientId',
      message: 'Use apenas letras, números, ponto, hífen ou sublinhado na referência externa.',
    });
  }

  return errors;
}
