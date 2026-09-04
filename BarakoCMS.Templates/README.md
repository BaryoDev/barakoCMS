<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/BarakoCMS.Templates/assets/icon.png" width="96" height="96" alt="BarakoCMS.Templates logo" />
  <h1>BarakoCMS.Templates</h1>
  <p><em>dotnet new templates for barakoCMS.</em></p>
</div>

---

```sh
dotnet new install BarakoCMS.Templates
dotnet new barakocms-module -n Acme.Notes
cd Acme.Notes
dotnet test
```

## barakocms-module

A module that builds, registers, and passes its own tests, so you start by deleting rather than
assembling. `-n Acme.Notes` gives a module named `Notes` in the `Acme.Notes` package:

```
Acme.Notes/
  Acme.Notes.slnx
  global.json                    opts dotnet test into Microsoft.Testing.Platform, which xunit.v3 needs
  Directory.Build.props          shared packaging metadata: the barakocms-module tag, icon, README
  Directory.Packages.props       package versions, in one place
  src/Acme.Notes/
    NotesModule.cs               IBarakoModule: contract version, scoped configuration, schema, seeder
    NotesCapabilities.cs         what the endpoint asks for, granted to Admin by the seeder
    NotesOptions.cs              bound from Modules:Notes
    Note.cs                      the document type the module owns
    Features/Notes/List/         GET /api/notes/notes, gated on the capability, bounded, tenant scoped
    README.md                    the package page
    assets/icon.png              a placeholder to replace
  tests/Acme.Notes.Tests/
    NotesModuleTests.cs          three tests on BarakoCMS.Testing, over a real PostgreSQL
```

Options:

| Option | Default | What it sets |
|---|---|---|
| `--BarakoCMSVersion` | the core this template shipped beside | the `BarakoCMS` package version |
| `--TestingVersion` | the harness it shipped beside | the `BarakoCMS.Testing` package version |
| `--Author` | `Module authors` | `<Authors>` in the shared props |
| `--License` | `MIT` | `<PackageLicenseExpression>` in the shared props |

The tests need Docker. [MODULES.md](https://github.com/BaryoDev/barakoCMS/blob/master/MODULES.md)
is the module contract and has the publishing conventions.

## Part of barakoCMS

This package belongs to [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source headless
CMS for .NET 10.

Licensed under MPL-2.0. What the template produces is yours, under whatever licence you pick.
