<div align="center">
  <img src="assets/icon.png" width="96" height="96" alt="MyBarakoModule logo" />
  <h1>MyBarakoModule</h1>
  <p><em>One line on what this module adds to barakoCMS.</em></p>
</div>

---

Two or three sentences on what it does and who it is for.

## Enable it

Reference the package and restart; barakoCMS discovers modules from the host's dependencies.

```sh
dotnet add package MyBarakoModule
```

A host that names its modules adds it by hand:

```csharp
builder.Services.AddBarakoCMS(builder.Configuration, modules =>
{
    modules.Add(new MyBarakoModule.ModuleNameModule());
});
```

## Configuration

Read from the module's own section, never the application root.

```json
{
  "Modules": {
    "ModuleName": { "Greeting": "Hello" }
  }
}
```

## Endpoints

| Method and path | Purpose | Capability |
|---|---|---|
| `GET /api/modulename/notes` | List the tenant's notes, paged | `read_modulename_notes` (granted to Admin at seed) |

## Compatibility

Written against barakoCMS module contract v1 and compiled against BarakoCMS BARAKOCMS_VERSION.

## Built for barakoCMS

A module for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source headless CMS for
.NET 10. Every module carries the `barakocms-module` tag, so one search on nuget.org returns the
whole set.
