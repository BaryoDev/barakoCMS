using FastEndpoints;

namespace BarakoCMS.Contacts.Features.Contacts.Update;

internal sealed class UpdateContactValidator : Validator<UpdateContactRequest>
{
    public UpdateContactValidator() => ContactSupport.AddRules(this);
}
