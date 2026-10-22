using FastEndpoints;

namespace BarakoCMS.Contacts.Features.Contacts.Create;

internal sealed class CreateContactValidator : Validator<CreateContactRequest>
{
    public CreateContactValidator() => ContactSupport.AddRules(this);
}
