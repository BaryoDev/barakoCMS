using System.Text.RegularExpressions;
using barakoCMS.Features.WorkflowsV2.Models;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Actions.Validation;

/// <summary>
/// Validate a field value (pre-hook action).
/// </summary>
public class ValidateFieldAction : BaseWorkflowAction
{
    public ValidateFieldAction(ILogger<ValidateFieldAction> logger) : base(logger)
    {
    }

    public override string Type => "ValidateField";
    public override string Name => "Validate Field";
    public override string Description => "Validate a field value against rules. Can block the operation if validation fails.";
    public override string Category => ActionCategories.Validation;

    public override bool SupportsPreHook => true;
    public override bool SupportsPostHook => false;
    public override bool CanBlockOperation => true;

    public override Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context)
    {
        try
        {
            var field = GetRequiredString(action.Config, "field", context);
            var errorMessage = GetString(action.Config, "errorMessage", context,
                $"Validation failed for field '{field}'");

            // Get the field value
            object? value;
            if (field.StartsWith("data.", StringComparison.OrdinalIgnoreCase))
            {
                var dataField = field.Substring(5);
                context.Content.Data.TryGetValue(dataField, out value);
            }
            else
            {
                context.Content.Data.TryGetValue(field, out value);
            }

            var validationErrors = new List<string>();

            // Required check
            if (GetBool(action.Config, "required", false))
            {
                if (value == null || (value is string s && string.IsNullOrWhiteSpace(s)))
                {
                    validationErrors.Add("Field is required.");
                }
            }

            if (value != null)
            {
                var strValue = value.ToString() ?? "";

                // Min length
                var minLength = GetInt(action.Config, "minLength", 0);
                if (minLength > 0 && strValue.Length < minLength)
                {
                    validationErrors.Add($"Minimum length is {minLength} characters.");
                }

                // Max length
                var maxLength = GetInt(action.Config, "maxLength", 0);
                if (maxLength > 0 && strValue.Length > maxLength)
                {
                    validationErrors.Add($"Maximum length is {maxLength} characters.");
                }

                // Pattern (regex)
                var pattern = GetString(action.Config, "pattern", context);
                if (!string.IsNullOrEmpty(pattern))
                {
                    try
                    {
                        if (!Regex.IsMatch(strValue, pattern))
                        {
                            var patternMessage = GetString(action.Config, "patternMessage", context,
                                "Value does not match required pattern.");
                            validationErrors.Add(patternMessage);
                        }
                    }
                    catch (ArgumentException ex)
                    {
                        Logger.LogWarning(ex, "Invalid regex pattern: {Pattern}", pattern);
                    }
                }

                // Numeric validations
                if (double.TryParse(strValue, out var numValue))
                {
                    // Min value
                    if (action.Config.TryGetValue("min", out var minObj) &&
                        double.TryParse(minObj?.ToString(), out var min))
                    {
                        if (numValue < min)
                        {
                            validationErrors.Add($"Value must be at least {min}.");
                        }
                    }

                    // Max value
                    if (action.Config.TryGetValue("max", out var maxObj) &&
                        double.TryParse(maxObj?.ToString(), out var max))
                    {
                        if (numValue > max)
                        {
                            validationErrors.Add($"Value must be at most {max}.");
                        }
                    }
                }

                // Enum validation
                var allowedValues = GetStringList(action.Config, "enum", context);
                if (allowedValues.Count > 0)
                {
                    if (!allowedValues.Any(v => v.Equals(strValue, StringComparison.OrdinalIgnoreCase)))
                    {
                        validationErrors.Add($"Value must be one of: {string.Join(", ", allowedValues)}.");
                    }
                }

                // Type validation
                var expectedType = GetString(action.Config, "type", context);
                if (!string.IsNullOrEmpty(expectedType))
                {
                    var isValid = expectedType.ToLowerInvariant() switch
                    {
                        "email" => IsValidEmail(strValue),
                        "url" => IsValidUrl(strValue),
                        "uuid" or "guid" => Guid.TryParse(strValue, out _),
                        "integer" or "int" => long.TryParse(strValue, out _),
                        "number" or "decimal" => double.TryParse(strValue, out _),
                        "date" => DateTime.TryParse(strValue, out _),
                        "boolean" or "bool" => bool.TryParse(strValue, out _) ||
                            strValue == "0" || strValue == "1",
                        _ => true
                    };

                    if (!isValid)
                    {
                        validationErrors.Add($"Value must be a valid {expectedType}.");
                    }
                }
            }

            // Custom validation function (via expression)
            var customValidation = GetString(action.Config, "validate", context);
            if (!string.IsNullOrEmpty(customValidation))
            {
                // Simple expression support - can be extended
                if (customValidation.StartsWith("length(") && customValidation.EndsWith(")"))
                {
                    // Parse length(min, max)
                    var args = customValidation.Substring(7, customValidation.Length - 8)
                        .Split(',')
                        .Select(s => s.Trim())
                        .ToArray();

                    if (args.Length == 2 && int.TryParse(args[0], out var lenMin) &&
                        int.TryParse(args[1], out var lenMax))
                    {
                        var len = (value?.ToString() ?? "").Length;
                        if (len < lenMin || len > lenMax)
                        {
                            validationErrors.Add($"Length must be between {lenMin} and {lenMax}.");
                        }
                    }
                }
            }

            if (context.IsDryRun)
            {
                Logger.LogInformation("[DRY-RUN] Validation for {Field}: {Result}",
                    field, validationErrors.Count == 0 ? "PASS" : "FAIL");

                return Task.FromResult(Success(new Dictionary<string, object>
                {
                    ["dryRun"] = true,
                    ["field"] = field,
                    ["valid"] = validationErrors.Count == 0,
                    ["errors"] = validationErrors
                }));
            }

            if (validationErrors.Count > 0)
            {
                var fullMessage = $"{errorMessage}: {string.Join(" ", validationErrors)}";
                Logger.LogWarning("Validation failed for {Field}: {Errors}", field, validationErrors);

                return Task.FromResult(Block(fullMessage));
            }

            Logger.LogInformation("Validation passed for {Field}", field);

            return Task.FromResult(Success(new Dictionary<string, object>
            {
                ["field"] = field,
                ["valid"] = true
            }));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Validation error");
            return Task.FromResult(Failure($"Validation error: {ex.Message}"));
        }
    }

    private bool IsValidEmail(string value)
    {
        try
        {
            var addr = new System.Net.Mail.MailAddress(value);
            return addr.Address == value;
        }
        catch
        {
            return false;
        }
    }

    private bool IsValidUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public override List<string> ValidateConfig(Dictionary<string, object> config)
    {
        var errors = new List<string>();

        if (!config.ContainsKey("field"))
            errors.Add("'field' is required.");

        return errors;
    }

    public override ActionConfigSchema GetConfigSchema()
    {
        return new ActionConfigSchema
        {
            Type = Type,
            Properties = new List<ActionConfigProperty>
            {
                new() { Name = "field", Type = "string", Description = "Field to validate", Required = true },
                new() { Name = "required", Type = "boolean", Description = "Whether the field is required" },
                new() { Name = "minLength", Type = "integer", Description = "Minimum string length" },
                new() { Name = "maxLength", Type = "integer", Description = "Maximum string length" },
                new() { Name = "min", Type = "number", Description = "Minimum numeric value" },
                new() { Name = "max", Type = "number", Description = "Maximum numeric value" },
                new() { Name = "pattern", Type = "string", Description = "Regex pattern to match" },
                new() { Name = "patternMessage", Type = "string", Description = "Custom message for pattern mismatch" },
                new() { Name = "enum", Type = "array", Description = "Allowed values" },
                new() { Name = "type", Type = "string", Description = "Expected type (email, url, uuid, integer, number, date, boolean)", Enum = new List<string> { "email", "url", "uuid", "integer", "number", "date", "boolean" } },
                new() { Name = "errorMessage", Type = "string", Description = "Custom error message" }
            },
            Required = new List<string> { "field" },
            Example = @"{""field"": ""data.email"", ""required"": true, ""type"": ""email"", ""errorMessage"": ""Please provide a valid email address""}"
        };
    }
}
