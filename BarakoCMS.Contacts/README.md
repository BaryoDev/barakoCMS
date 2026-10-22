# BarakoCMS.Contacts

One record for a person or an organization. A CRM tracks it as a lead or a company, a survey tool as
a respondent, a membership back office as a member, an invoicing app as a customer. The record is the
same; the tenant adds the fields its own use needs.

## Install

```sh
dotnet add package BarakoCMS.Contacts
```

The package reference plus a restart is the install. `BarakoCMS:Modules:Enabled` decides which
modules run (`BarakoCMS__Modules__Enabled=Contacts`). To name it by hand, put
`modules.Add(new BarakoCMS.Contacts.ContactsModule())` in the `AddBarakoCMS` callback.

## The record

A contact has a `kind` (`Person` or `Organization`), a `displayName`, optional `givenName`,
`familyName` and `website`, and the channels `email`, `phone` and `address`. Everything else is a
custom field.

Custom fields are declared on the `contact-record` content type with the ordinary content type
endpoints (`/api/content-types`), and every create and update is validated against it. A tenant that
has not declared its own copy is served the built-in one. Send custom values under `fields`; the
standard names go at the top level, and a standard name inside `fields` is a 400.

## Routes

| Route | Needs | Does |
| --- | --- | --- |
| `POST /api/contacts` | `manage_contacts` | Create. 409 when the email is held. |
| `GET /api/contacts` | `read_contacts` | Page of contacts. Filters: `kind`, `name`, `email`. |
| `GET /api/contacts/{id}` | `read_contacts` | One contact, channels included. |
| `PUT /api/contacts/{id}` | `manage_contacts` | Replace. 409 when the email is held by another. |
| `DELETE /api/contacts/{id}` | `manage_contacts` | Delete the contact and its links. |
| `POST /api/contacts/{id}/merge` | `manage_contacts` | Fold a duplicate into `intoId`. |
| `POST /api/contacts/{id}/links` | `manage_contacts` | Link a person to an organization, with a role. |
| `GET /api/contacts/{id}/links` | `read_contacts` | Links of a person or an organization. |
| `DELETE /api/contacts/{id}/links/{organizationId}` | `manage_contacts` | Remove a link. |
| `GET /api/public/contacts`, `/{id}` | anonymous | Published contacts, Public fields only. |

Both capabilities go to the Admin role by default. Contacts are per tenant: a caller never sees,
links or merges a contact of another tenant, and another tenant's id answers 404.

## Deduplication and merge

The email, trimmed and lower cased, is unique per tenant. The schema holds it, so two creates of one
address that race cannot both win: the loser gets a 409 naming the existing contact. A contact with
no email is never a duplicate of another.

To resolve a 409, either update the contact it names, or merge:

```
POST /api/contacts/{duplicateId}/merge
{ "intoId": "<survivor id>" }
```

The survivor keeps every value it has and takes the duplicate's where it has none (custom fields
included). The duplicate's links move to the survivor, without doubling one the survivor already has.
The duplicate stays as a tombstone with `mergedInto` set and its personal fields cleared, so an old
reference to its id can still be followed. It no longer appears in lists, reads or delivery. Both
contacts must be the same kind.

## Public delivery

A contact is served anonymously only when it is `isPublished` and the tenant's `contact-record` type
is publicly deliverable (off by default). Email, phone and address are never handed to the
projector, so a type that marks one of them Public by mistake still cannot serve it. Custom fields
follow their declared sensitivity.

## One record, two uses

CRM. Declare `leadStatus` (choice: new, qualified, won) and `owner` on `contact-record`. Create the
company as an `Organization`, each person as a `Person`, link them with a role such as `Buyer`. When
the same lead arrives from a second source with the same email, merge it into the first.

Survey. Declare `surveyId` and `consent` on `contact-record`. Create each respondent as a `Person`
with only a `displayName` and an email. A returning respondent hits the 409, and the response
submission uses the contact it names instead of making a second record. Nothing is published, so
nothing is served anonymously.

The admin screen lives in the console (barakoBrew), which drives these routes.
