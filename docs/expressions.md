# Expressions

The formula language of a [computed field](computed-fields.md). A formula reads the fields of one
entry and gives one value. It has no variables, loops or user functions, and the function list
below is fixed.

## Syntax

| Part | Written as |
| :--- | :--- |
| Field | `Qty`, or `data.Qty`. Names match without regard to case |
| Number | `12`, `0.5`. No exponent form, so `1e3` is refused |
| Text | `"text"` in double quotes. `\"` and `\\` are the only escapes |
| Bool | `true`, `false` |
| Date | A field of type `date` or `datetime`, or `date("2026-11-22")` for a day |
| Null | `null` fits any type |
| Arithmetic | `+ - * /` on numbers, and unary `-` |
| Comparison | `= != < <= > >=`. Both sides have the same type. A bool is compared only with `=` and `!=` |
| Logic | `and`, `or`, `not` |
| Grouping | `( )` |

From loosest to tightest: `or`, `and`, `not`, comparison, `+ -`, `* /`, unary minus. A comparison
is not chained. Operators and function names are not case sensitive.

The system names `id`, `status`, `createdAt` and `updatedAt` read the entry itself. A stored
computed field may not read them. A field of one of those names is read as `data.Name`.

## Nulls

A missing or blank field reads as null. Arithmetic, comparison with `<`, `<=`, `>` and `>=`, and
most functions give null when an argument is null. `=` and `!=` treat null as a value that equals
only null. `and` and `or` follow three-valued logic: `false and null` is false and `true or null`
is true. `iif`, `switch`, `coalesce`, `concat` and `isblank` take null as an input.

Division by zero and a number out of range give null, and the evaluation reports a note.

## Functions

Number functions:

| Function | Gives |
| :--- | :--- |
| `round(n)`, `round(n, places)` | `n` rounded half away from zero to `places` (0 to 10, a whole number, 0 when left out) |
| `floor(n)` | The largest whole number not above `n` |
| `ceiling(n)` | The smallest whole number not below `n` |
| `abs(n)` | The absolute value |
| `min(a, b, ...)`, `max(a, b, ...)` | The least or greatest of two or more numbers |

Logic functions. `iif`, `switch` and `coalesce` run only the argument they pick:

| Function | Gives |
| :--- | :--- |
| `iif(condition, a, b)` | `a` when the condition is true, otherwise `b`. A null condition gives `b` |
| `switch(c1, v1, c2, v2, ..., default)` | The value after the first true condition, otherwise the default |
| `coalesce(a, b, ...)` | The first argument that is not blank. Null, and text of only white space, are blank |
| `isblank(x)` | True for null, and for text of only white space |

The arguments of `iif`, `switch` and `coalesce` that can be the result share one type.

Text functions. Comparison is ordinal and case change is invariant, with no culture and no
pattern matching:

| Function | Gives |
| :--- | :--- |
| `concat(a, b, ...)` | The arguments that are not blank, joined as written. Numbers and dates are written as text. Null when none is left |
| `upper(t)`, `lower(t)` | The text in upper or lower case |
| `trim(t)` | The text without white space at both ends |
| `left(t, n)`, `right(t, n)` | The first or last `n` characters. `n` is a whole number, 0 or more |
| `len(t)` | The number of characters |
| `replace(t, find, with)` | Every occurrence of `find` replaced. `find` may not be empty |
| `contains(t, part)` | True when `t` holds `part` |
| `startsWith(t, part)` | True when `t` starts with `part` |

Date functions. A time of day is kept, so these work on a `datetime` too:

| Function | Gives |
| :--- | :--- |
| `years(from, to)` | Whole years, rounded toward zero |
| `months(from, to)` | Whole months. The anniversary is the same day of the month, or the last day when that month is shorter. A later `from` gives a negative |
| `days(from, to)` | Days between, as a number with a fraction when a time of day is present |
| `hours(from, to)` | Hours between, as a number with a fraction |
| `addDays(d, n)` | The date `n` days later. `n` is a whole number, at most 3652059 either way |
| `addMonths(d, n)` | The date `n` months later. `n` is a whole number, at most 120000 either way |
| `year(d)`, `month(d)`, `day(d)` | A part of the date, as a number |
| `today()`, `now()` | The current UTC date, or date and time |

`today()` and `now()` are for formulas evaluated on demand. A stored computed field may not use
them, because its value would go stale.

There is no `minutes`. A duration in minutes is `hours(A, B) * 60`.

`format(value, pattern)` gives text. The pattern is a quoted literal from this list and nothing
else:

| Value | Patterns |
| :--- | :--- |
| Number | `0`, `0.0`, `0.00`, `0.000`, `#,##0`, `#,##0.00`, `0%`, `0.0%` |
| Date | `yyyy-MM-dd`, `yyyy-MM-dd HH:mm`, `dd/MM/yyyy`, `MM/dd/yyyy`, `d MMM yyyy`, `MMMM d, yyyy`, `MMM yyyy`, `yyyy`, `HH:mm` |

A number is rounded half away from zero, and every result uses the invariant culture. A percent
pattern multiplies by 100.

## Bounds

Each bound is checked when the formula is parsed and again when it is evaluated, because a stored
formula can predate a bound.

| Bound | Limit |
| :--- | :--- |
| Source length | 2000 characters |
| Parts (nodes) | 500 |
| Depth (nesting and tree height) | 32 |
| Text length, for any text a formula builds | 10000 characters |
| Work per evaluation | 2000 steps. One per node, and one per 32 characters a text function reads or writes |
| `round` places | 0 to 10 |

A formula that breaks a bound at save time is refused. One that breaks a bound while it runs gives
no value for the whole formula, so `coalesce` cannot turn the failure into a different answer.

## Errors

A refusal names the rule and the position, and never an entry value or the text of a string in the
formula. The rules are: `empty`, `source-too-long`, `node-limit`, `depth-limit`, `text-too-long`,
`step-budget`, `unexpected-character`, `bad-number`, `exponent`, `bad-string`, `bad-date`,
`syntax`, `member-access`, `unknown-name`, `ambiguous-name`, `unknown-function`, `arity`,
`type-mismatch`, `argument-range`, `format-pattern`, `clock-function`, `overflow`,
`division-by-zero` and `value-type`.
