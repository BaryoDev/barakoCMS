- **A `devsite` blueprint: the shape barakocms.com needs.** `page`, `post`, `category`, `author` and a
  flat `doc` type carrying its own section, order and parent fields for a documentation tree, plus
  four types meant to be filled by a collection sync (#794) rather than typed by hand: `package`
  (NuGet), `release` and `contributor` (GitHub), `up-for-grabs` (open issues). Every reference in it
  points at a type the same blueprint declares, so it applies on a fresh tenant with nothing else
  applied first, and applying it twice is refused with 409 like any other blueprint. Ships under the
  existing `barakoCMS/Blueprints` directory rather than a separate repository, since nothing outside
  this project reads a kit yet; moving it later is one JSON file. (closes #721)
