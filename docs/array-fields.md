# Array fields

An `array` field holds a JSON list. With no `itemType` it accepts any list, which is what every
array stored before 5.0 does.

## Typed items

Declare `itemType` to hold every item to one type:

```json
{ "name": "Links", "type": "array", "itemType": "url" }
```

The item types are `string`, `text`, `markdown`, `int`, `decimal`, `bool`, `date`, `datetime`,
`time`, `email`, `url`, `slug` and `uuid`, each checked the way a field of that type is. A write
holding an item of another type, or a null item, answers 400 and names the item's index:

```
Field 'Links' (Links) holds items of type 'url', and the item at index 1 is not one.
```

`itemType` is refused on a field that is not an array. A list an entry already holds is not
checked again when an edit leaves it as it is, so declaring an item type on a field that already
has entries does not block editing their other fields.

## Lists of references and options

A list of entry ids is a `reference` field with `multiple: true`, which checks that each id names an
entry of the declared type, and which `include` and the `has` filter read. `itemType: "reference"`
is refused for that reason. A list of options is a `choice` field with `multiple: true`.
