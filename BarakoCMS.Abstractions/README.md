<div align="center">
  <img src="https://raw.githubusercontent.com/BaryoDev/barakoCMS/master/assets/icon.png" width="96" height="96" alt="BarakoCMS.Abstractions logo" />
  <h1>BarakoCMS.Abstractions</h1>
  <p><em>The barakoCMS package contract, on its own, with no reference to the host.</em></p>
</div>

---

This is what a module compiles against: the module interfaces, the documents and events a consumer
stores and reads, the service interfaces the core resolves, and the workflow extension points.
It carries no endpoints, no startup and no host behaviour.

The contract used to be a list in the repository's coding standard, and the only thing stopping a
module reaching past it into the host was review. Here it is the assembly boundary, so a type that
is not contract is a compile error rather than a note in a pull request.

## What is in it

| Namespace | What it holds |
|---|---|
| `barakoCMS.Modules` | `IBarakoModule`, `IModuleSchema`, `ModuleCapabilities`, `ModuleContract` |
| `barakoCMS.Models` | The documents: content, content types, users, roles, permissions, workflows, jobs |
| `barakoCMS.Events` | The content event stream |
| `barakoCMS.Core.Interfaces` | `IEmailService`, `IOtpService`, `ISmsService`, `IContentWriter`, `ISensitivityService` and the rest of the service seams |
| `barakoCMS.Features.Workflows` | `IWorkflowAction`, `IWorkflowEngine`, `WorkflowActionResult` |

Namespaces are unchanged from when these types shipped inside `BarakoCMS`, so a module moving to
this package edits no `using`.

## Using it

```sh
dotnet add package BarakoCMS.Abstractions
```

A first-party module references it alongside `BarakoCMS`. On its own it gives you the types to
implement `IBarakoModule` and a custom `IWorkflowAction`; the host that runs them is `BarakoCMS`.

## Stability

The types here are the package surface described in the repository's `CLAUDE.md` section 6. Within
a major version they do not lose or change a public member. An addition arrives as a new overload
or an interface member with a default implementation, so an existing implementor still compiles.

## Part of barakoCMS

This is the contract package for [barakoCMS](https://github.com/BaryoDev/barakoCMS), an open-source
headless CMS for .NET 10. Every module is published under the `barakocms-module` tag, so a single
search on nuget.org returns the whole set.
