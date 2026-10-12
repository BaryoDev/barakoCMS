# Contributor terms

The barakoCMS core is [MPL-2.0](LICENSE) and the modules are MIT (D24 in
[DECISIONS.md](DECISIONS.md)). Your contribution ships under the licence of where it lands, you
keep your copyright, and barakoCMS is not released as closed source.

| Where the contribution lands | Licence |
| --- | --- |
| `barakoCMS/` (the core) and `BarakoCMS.Abstractions/` (the contract split out of it) | MPL-2.0 |
| the modules (every other `BarakoCMS.*` package folder), `BarakoCMS.Testing`, `BarakoCMS.Suite`, `BarakoCMS.Templates` and the module template | MIT, the `LICENSE` file in that folder (`BarakoCMS.Testing` and `BarakoCMS.Templates` get theirs with their 5.0 release, which is when they move to MIT) |
| anything else in the repository, `BarakoCMS.Tests/`, docs, scripts, migrations and CI included | MPL-2.0, the root `LICENSE` |

Each package folder that is MIT carries its own `LICENSE`, and `scripts/check-licences.sh` checks
the licence every package declares against this table.

Worth splitting that last part honestly, because it is a structural guarantee for some of the code
and a commitment for the rest:

- **Contributions accepted under these terms cannot be released closed.** The permission granted
  below does not extend that far, so it is not a matter of anyone's intentions.
- **Contributions accepted before 23 August 2026** were made under terms that did grant a right to
  relicense, including commercially. Those rights exist and will not be exercised. That is a
  commitment rather than a structural limit, and it is stated plainly rather than papered over.

That last point is about *our* releases. MPL-2.0 lets anyone, you included, use barakoCMS inside a
commercial or closed-source product. That is the licence working as intended and nothing here
changes it.

There is nothing to sign. Opening a pull request means you agree to what is below.

These terms apply from 23 August 2026 and are not retroactive: contributions made earlier stay
under the terms published at the time.

## What you are agreeing to

**1. You give us permission to use your contribution.**
A perpetual, worldwide, royalty-free licence to use, modify, distribute and sublicense what you
contribute, so it can ship as part of barakoCMS and any project that includes it.

**2. MPL-2.0 or MIT. Nothing else.**
Releases containing your contribution are made under the licence the table above gives for where
it lands: MPL-2.0 for the core, MIT for the modules. A contribution to the core may also be
released under MIT if the core ever moves. Those are the only two licences BaryoDev uses, and naming them is more useful than saying
"an open source licence": that phrase covers AGPL, which many companies will not accept, so you
would still have to ask what could happen. Now you do not.

It was Apache-2.0 up to 3.1.1, so a move has happened once and is a real possibility rather than a
hypothetical.

We cannot make a closed-source release of your work. Not "will not": the permission does not
extend that far, so there is nothing to change our minds about later.

**3. You keep your copyright.**
Nothing here transfers ownership. It is a licence, not an assignment. Your contribution stays
yours and you can use it anywhere else you like, including in commercial work of your own.

**4. Patents.**
For a contribution to the core, MPL-2.0 section 2.1(b) is the patent grant and it applies to your
contribution. Pointed at rather than restated, because a second definition alongside the licence's
is a second one to keep in step.

The MIT licence says nothing about patents, so for a contribution that lands under MIT you grant
the patent licence here: a perpetual, worldwide, no-charge, royalty-free, irrevocable (except as
stated in this section) licence, to everyone who receives the software, to make, have made, use,
offer to sell, sell, import and otherwise transfer the contribution, under the patent claims you can
license that are necessarily infringed by your contribution alone or by its combination with the
package it was contributed to. If anyone starts patent litigation against any entity, a cross-claim
or counterclaim included, alleging that the contribution or that package infringes a patent, the
patent licences granted to them under this section end on the date that litigation is filed. This
is the patent clause of Apache-2.0 section 3, applied to your contribution and the package it lands
in, because it is the one most legal teams already know.

Either way the purpose is the same: a contribution cannot become a legal trap for everyone
downstream.

**5. You have the right to contribute it.**
Either you wrote it, or you have permission from whoever did. If your employer owns your work,
please check before contributing. This clause exists to stop you accidentally causing yourself a
problem, not to catch you out.

**6. No warranty, and no obligation.**
Your contribution is provided as is. You are not signing up to maintain it, answer questions about
it, or fix it later. If you disappear tomorrow, that is fine and expected.

## What is deliberately not here

**No copyright assignment.** Some projects ask you to hand over ownership. This one does not.

**No right to relicense your work as proprietary.** An earlier version of this document granted
exactly that, and advertised commercial licensing and dual licensing as reasons for it. Both were
removed because they contradicted what barakoCMS promises: free at any scale, with no paid edition
and no threshold that starts a bill. A permission we would never use is a permission worth not
holding.

**No clause about payment.** The old version said you waive any right to compensation. Contributions
are voluntary and that goes without saying, so it now goes unsaid.

**No signing step.** There used to be a bot. It was never a required status check, so it blocked
nothing while looking like it did, which is the worst of both. Contributing is the agreement.

## Questions

Open an issue, or ask on the pull request. If something here worries you, say so before you spend
time on the work rather than after.
