using System.Text.RegularExpressions;
using AWFace.Api.Contracts;

namespace AWFace.Api.Services;

public static partial class JourneyValidator
{
    public static IReadOnlyList<object> Validate(JourneyStartRequest request)
    {
        var errors = new List<object>();

        if (string.IsNullOrWhiteSpace(request.IntegrationToken))
        {
            errors.Add(new { field = "integrationToken", message = "Informe o token de integração." });
        }

        if (!IsValidCpf(request.Cpf))
        {
            errors.Add(new { field = "cpf", message = "Informe um CPF válido." });
        }

        if (!IsCompleteName(request.FullName))
        {
            errors.Add(new { field = "fullName", message = "Informe nome e sobrenome." });
        }

        if (request.BirthDate >= DateOnly.FromDateTime(DateTime.Today) ||
            request.BirthDate < DateOnly.FromDateTime(DateTime.Today.AddYears(-120)))
        {
            errors.Add(new { field = "birthDate", message = "Informe uma data de nascimento válida." });
        }

        if (string.IsNullOrWhiteSpace(request.ExternalClientId))
        {
            errors.Add(new { field = "externalClientId", message = "Informe a referência externa da jornada." });
        }
        else if (request.ExternalClientId.Length > 255)
        {
            errors.Add(new { field = "externalClientId", message = "A referência externa deve ter até 255 caracteres." });
        }
        else if (!ExternalIdRegex().IsMatch(request.ExternalClientId))
        {
            errors.Add(new { field = "externalClientId", message = "Use apenas letras, números, ponto, hífen ou sublinhado na referência externa." });
        }

        return errors;
    }

    private static bool IsCompleteName(string fullName)
    {
        return fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(part => part.Length >= 2) >= 2;
    }

    private static bool IsValidCpf(string cpf)
    {
        var digits = DigitsOnly(cpf);
        if (digits.Length != 11 || digits.Distinct().Count() == 1)
        {
            return false;
        }

        static int Calc(string source, int factor)
        {
            var total = 0;
            foreach (var digit in source)
            {
                total += (digit - '0') * factor--;
            }

            var rest = total * 10 % 11;
            return rest == 10 ? 0 : rest;
        }

        return Calc(digits[..9], 10) == digits[9] - '0' &&
               Calc(digits[..10], 11) == digits[10] - '0';
    }

    public static string DigitsOnly(string value)
    {
        return new string(value.Where(char.IsDigit).ToArray());
    }

    [GeneratedRegex("^[a-zA-Z0-9_.-]+$")]
    private static partial Regex ExternalIdRegex();
}
