# BarakoCMS.Qr

Renders one field of a public entry as a QR code, PNG or SVG. Made for a claim token a desk scans at
pickup, and usable as an image source in a page or an email.

## Install

```sh
dotnet add package BarakoCMS.Qr
```

The package reference plus a restart is the install. See `MODULES.md` in the repository for how
modules are found and enabled (`BarakoCMS__Modules__Enabled=Qr`).

## Configure

Nothing is exposed until you list the fields. Type and field names are case-insensitive.

```json
{
  "Modules": {
    "Qr": {
      "Fields": { "registration": [ "ClaimToken" ] },
      "MaxValueLength": 1024,
      "MaxPixelsPerModule": 20
    }
  }
}
```

## Endpoint

| Method | Route | Who | Purpose |
|---|---|---|---|
| GET | `/api/public/{type}/{id}/qr/{field}?format=png\|svg&size=8` | anyone | the field's value as a QR code |

`format` defaults to `png`. `size` is pixels per QR module, 1 up to `MaxPixelsPerModule`, default 8.
A bad `format` or `size` is a 400.

It answers 404 when the field is not listed, the type is not publicly deliverable, the entry is
missing, a draft or not Public, the field is not Public on its type, or the value is empty or longer
than `MaxValueLength`. The entry goes through the same projector as the core public routes.
