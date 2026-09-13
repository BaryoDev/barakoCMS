- **A large release published every package and then failed to tag.** GitHub refuses a release body
  over 125,000 characters, 4.0.0's changelog section was 196,028, and the release job only wrote the
  body after NuGet, GitHub Packages and the images were already out (#660).
  `scripts/release-notes.sh` now summarises a section over 120,000 characters: Breaking and Security
  stay in full and every other subsection becomes a count and a link to `CHANGELOG.md`. For 4.0.0
  that is 41,002 characters. A section that fits prints exactly as before. If even the summary does
  not fit, the script exits non-zero, and the release workflow now runs it in the gate job, before
  anything is published.
