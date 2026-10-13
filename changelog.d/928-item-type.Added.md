- **An `array` field accepted any list, so a list of links could hold a number.** An `array` field
  may now declare `itemType`, one of `string`, `text`, `markdown`, `int`, `decimal`, `bool`, `date`,
  `datetime`, `time`, `email`, `url`, `slug` or `uuid`. A write holding an item of another type, or
  a null item, is a 400 naming the item's index. The type's delivery OpenAPI describes the items.
  An array with no item type accepts what it accepted before, and a list an entry already holds is
  not checked again on an edit of another field. A list of references stays a `reference` field
  with `multiple`, and `itemType: "reference"` is refused with that advice.
