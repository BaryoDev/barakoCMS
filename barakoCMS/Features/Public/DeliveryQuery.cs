using System.Globalization;
using barakoCMS.Models;

namespace barakoCMS.Features.Public;

internal enum FilterOp { Eq, Ne, Lt, Lte, Gt, Gte, Contains }

/// <summary>One validated field comparison, safe to translate into SQL.</summary>
/// <param name="Field">A field name the content type marks Public. Never caller-supplied text.</param>
/// <param name="Type">
/// The field's declared type. Carried because jsonb compares by type first: without it a filter on
/// a string field holding "500" would emit the number 500 and match nothing.
/// </param>
internal readonly record struct DeliveryFilter(string Field, FilterOp Op, string Value, string Type);

/// <summary>A validated sort, or none.</summary>
internal readonly record struct DeliverySort(string Field, bool Descending);

/// <summary>
/// A validated proximity filter: everything within <paramref name="RadiusKm"/> of a centre.
/// </summary>
/// <param name="Field">A geopoint field the content type marks Public. Never caller-supplied text.</param>
internal readonly record struct DeliveryNear(string Field, double Lat, double Lng, double RadiusKm);

/// <summary>The outcome of parsing the query string: either a query to run, or the reason it was refused.</summary>
internal sealed class DeliveryQuery
{
    public const int MaxFilters = 5;

    /// <summary>
    /// The largest radius a near filter may ask for, in kilometres, unless
    /// <c>Delivery:MaxRadiusKm</c> says otherwise.
    /// </summary>
    /// <remarks>
    /// The bounding box is what keeps a proximity query from computing a haversine for every row of
    /// the type, and a radius wider than a continent makes the box the whole table. A thousand
    /// kilometres covers "in this country" for most countries and keeps the prefilter meaningful.
    /// </remarks>
    public const double DefaultMaxRadiusKm = 1000;

    public IReadOnlyList<DeliveryFilter> Filters { get; init; } = Array.Empty<DeliveryFilter>();
    public DeliverySort? Sort { get; init; }
    public DeliveryNear? Near { get; init; }

    /// <summary>Order by the distance a near filter computed: null for no, else whether descending.</summary>
    public bool? DistanceSortDescending { get; init; }

    public string? Error { get; init; }

    public bool IsValid => Error is null;

    private static readonly Dictionary<string, FilterOp> Ops = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eq"] = FilterOp.Eq,
        ["ne"] = FilterOp.Ne,
        ["lt"] = FilterOp.Lt,
        ["lte"] = FilterOp.Lte,
        ["gt"] = FilterOp.Gt,
        ["gte"] = FilterOp.Gte,
        ["contains"] = FilterOp.Contains,
    };

    /// <summary>
    /// Parses <c>filter[field][op]=value</c> and <c>sort=field</c> / <c>sort=-field</c> against the
    /// fields a content type actually exposes.
    /// </summary>
    /// <remarks>
    /// The allowlist is built the same way <see cref="PublicDelivery.ToPublic"/> builds it, from
    /// fields marked <see cref="SensitivityLevel.Public"/>. That is deliberate and load-bearing:
    /// filtering on a field the caller cannot read is an oracle. A caller could binary-search a
    /// Sensitive salary or date of birth by observing which entries come back, without the value ever
    /// appearing in a response. Refusing unknown fields rather than ignoring them matters for the
    /// same reason a silently ignored filter returns more rows than the caller asked for, and the
    /// caller cannot tell the difference between "no filter" and "no matches".
    /// </remarks>
    public static DeliveryQuery Parse(
        IEnumerable<KeyValuePair<string, string?>> query, ContentTypeDefinition? def,
        double maxRadiusKm = DefaultMaxRadiusKm)
    {
        if (def is null)
            return new DeliveryQuery { Error = "Unknown content type." };

        var allowed = def.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public)
            .ToDictionary(f => f.Name, f => f.Type ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        var filters = new List<DeliveryFilter>();
        DeliveryNear? near = null;
        string? sortValue = null;

        foreach (var (rawKey, rawValue) in query)
        {
            if (string.Equals(rawKey, "sort", StringComparison.OrdinalIgnoreCase))
            {
                // Resolved after the loop. sort=distance is only meaningful next to a near filter,
                // and the query string does not promise which of the two comes first.
                var value = rawValue?.Trim();
                if (!string.IsNullOrEmpty(value))
                    sortValue = value;
                continue;
            }

            if (!rawKey.StartsWith("filter[", StringComparison.OrdinalIgnoreCase))
                continue;

            // filter[field][op]
            var parts = rawKey[7..].TrimEnd(']').Split("][", StringSplitOptions.None);
            if (parts.Length != 2)
                return new DeliveryQuery
                {
                    Error = $"Filter '{rawKey}' is malformed. Expected filter[field][op]=value.",
                };

            var (name, op) = (parts[0], parts[1]);

            if (!allowed.TryGetValue(name, out var declaredType))
                return new DeliveryQuery { Error = Unfilterable(name, allowed) };
            var canonical = Canonical(allowed, name);

            if (string.Equals(op, "near", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(declaredType, "geopoint", StringComparison.OrdinalIgnoreCase))
                    return new DeliveryQuery
                    {
                        Error = $"Operator 'near' needs a geopoint field, and '{canonical}' is not one.",
                    };
                if (near is not null)
                    return new DeliveryQuery { Error = "At most one near filter is allowed per request." };

                var (parsed, nearError) = ParseNear(canonical, rawValue, maxRadiusKm);
                if (nearError is not null)
                    return new DeliveryQuery { Error = nearError };
                near = parsed;
            }
            else
            {
                if (!Ops.TryGetValue(op, out var parsedOp))
                    return new DeliveryQuery
                    {
                        Error = $"Unknown operator '{op}'. Supported: {string.Join(", ", Ops.Keys)}, near.",
                    };

                // The canonical name from the schema is stored, never the caller's spelling, so what
                // reaches the query builder can only be a string the content type already declared.
                filters.Add(new DeliveryFilter(canonical, parsedOp, rawValue ?? string.Empty, declaredType));
            }

            // Arbitrary filter combinations against a JSONB column on an anonymous endpoint is a
            // denial-of-service surface, so the count is capped rather than left to the caller.
            // The near filter counts: it is the most expensive one.
            if (filters.Count + (near is null ? 0 : 1) > MaxFilters)
                return new DeliveryQuery
                {
                    Error = $"Too many filters. At most {MaxFilters} are allowed per request.",
                };
        }

        DeliverySort? sort = null;
        bool? distanceSort = null;
        if (sortValue is not null)
        {
            var descending = sortValue.StartsWith('-');
            var field = descending ? sortValue[1..] : sortValue;
            var isDistance = string.Equals(field, "distance", StringComparison.OrdinalIgnoreCase);

            // The computed distance wins over a field that happens to be called Distance, but only
            // when there is a distance to sort by. Without a near filter a field of that name keeps
            // working as it did, and a type without one gets told what is missing.
            if (isDistance && near is not null)
                distanceSort = descending;
            else if (allowed.ContainsKey(field))
                sort = new DeliverySort(Canonical(allowed, field), descending);
            else if (isDistance)
                return new DeliveryQuery
                {
                    Error = "sort=distance needs a near filter to measure from. Add filter[<field>][near]=lat,lng,radiusKm.",
                };
            else
                return new DeliveryQuery { Error = Unsortable(field, allowed) };
        }

        return new DeliveryQuery
        {
            Filters = filters, Sort = sort, Near = near, DistanceSortDescending = distanceSort,
        };
    }

    /// <summary>
    /// <c>lat,lng,radiusKm</c>, every part a finite number, the centre a real position and the
    /// radius positive and no wider than the cap.
    /// </summary>
    /// <remarks>
    /// Refused above the cap rather than clamped. A silently narrowed radius returns fewer rows than
    /// the caller asked for with nothing in the response saying so, which is the same fault as a
    /// silently ignored filter.
    /// </remarks>
    private static (DeliveryNear? Near, string? Error) ParseNear(string field, string? raw, double maxRadiusKm)
    {
        const string shape = "Expected filter[field][near]=lat,lng,radiusKm.";
        var parts = (raw ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
            return (null, $"Filter 'near' on '{field}' is malformed. {shape}");

        if (!TryNumber(parts[0], out var lat) || !TryNumber(parts[1], out var lng) || !TryNumber(parts[2], out var radius))
            return (null, $"Filter 'near' on '{field}' is malformed: every part must be a number. {shape}");

        if (lat < -90 || lat > 90 || lng < -180 || lng > 180)
            return (null, $"Filter 'near' on '{field}' has a centre outside latitude -90..90, longitude -180..180.");

        if (radius <= 0)
            return (null, $"Filter 'near' on '{field}' needs a radius above zero kilometres.");

        if (radius > maxRadiusKm)
            return (null, $"Filter 'near' on '{field}' asks for {radius.ToString(CultureInfo.InvariantCulture)} km. "
                          + $"The most a request may ask for is {maxRadiusKm.ToString(CultureInfo.InvariantCulture)} km.");

        return (new DeliveryNear(field, lat, lng, radius), null);
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    /// <summary>
    /// One filter as a SQL fragment plus its bound parameters, for Marten's <c>MatchesSql</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately not JSONPath. The <c>@?</c> JSONPath operator contains a <c>?</c>, which is also
    /// MatchesSql's parameter placeholder, and the overload that exists to resolve that collision
    /// (<c>MatchesJsonPath</c>) could not bind parameters at all until JasperFx/marten#5289 and
    /// #5293, both of which landed in Marten 9.30. This project was on 8.37 when that was written
    /// and is on 9.30 now, so the workaround is no longer forced. It is kept anyway, because
    /// extracting with <c>-&gt;</c> binds the field name as well as the value and the JSONPath form
    /// never could.
    ///
    /// Extracting with <c>-&gt;</c> avoids <c>?</c> entirely, so ordinary parameters work: the field
    /// name and the value are both bound, and neither reaches the SQL text. That is stronger than
    /// the JSONPath version would have been, where only the value could be bound.
    ///
    /// Comparison happens in jsonb rather than text, so 9 sorts below 10 instead of after it.
    /// </remarks>
    public static (string Sql, object[] Parameters) ToSql(DeliveryFilter f)
    {
        // ILIKE on the text projection: a substring match is a text question, and asking it of a
        // jsonb value would compare the quotes too.
        // #>> '{}' unwraps a jsonb scalar to text without its quotes, so a stored "hat" compares
        // as hat rather than "hat".
        if (f.Op == FilterOp.Contains)
            return ($"({KeyLookup} #>> '{{}}') ILIKE ?", [f.Field, $"%{Escape(f.Value)}%"]);

        var op = f.Op switch
        {
            FilterOp.Eq => "=",
            FilterOp.Ne => "<>",
            FilterOp.Lt => "<",
            FilterOp.Lte => "<=",
            FilterOp.Gt => ">",
            FilterOp.Gte => ">=",
            _ => throw new ArgumentOutOfRangeException(nameof(f)),
        };

        return ($"{KeyLookup} {op} ?::jsonb", [f.Field, JsonLiteral(f.Value, f.Type)]);
    }

    /// <summary>
    /// Finds a field's value by key, case-insensitively, the way the public projection does.
    /// </summary>
    /// <remarks>
    /// <c>ToPublic</c> matches the schema with <c>OrdinalIgnoreCase</c>, so a record holding
    /// "price" under a schema field named "Price" is delivered normally. PostgreSQL's <c>-&gt;</c>
    /// is case sensitive, so a filter built from the schema spelling would miss that record: it
    /// would appear in an unfiltered list and vanish from a filtered one, reading as "no matches"
    /// rather than as a fault. Delivery and filtering have to agree on what a key is.
    ///
    /// The cost is that this cannot use an expression index on the key, so a filtered list scans.
    /// Indexing strategy is deferred to the sorting half of #140; correctness comes first, and a
    /// fast wrong answer is not worth having.
    /// </remarks>
    private const string KeyLookup =
        "(SELECT e.value FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) LIMIT 1)";

    /// <summary>
    /// A case-insensitive equality match on one data field, for <c>MatchesSql</c>.
    /// </summary>
    /// <remarks>
    /// Written for the slug route, which used to load every published entry of the type and match in
    /// memory. On a blog with 20k posts that deserialized 20k documents to return one, and a 404
    /// probe cost the same.
    ///
    /// Not <c>ToSql</c> with <see cref="FilterOp.Eq"/>: that compares in jsonb, so the match would be
    /// case sensitive, while <c>PublicDelivery.SlugValue</c> compares with OrdinalIgnoreCase. Not
    /// <see cref="FilterOp.Contains"/> either: ILIKE would treat % and _ in a slug as wildcards, so
    /// a request for "a_b" could answer with "axb". <c>#&gt;&gt; '{}'</c> unwraps the jsonb scalar to
    /// text so a stored "hello" compares as hello rather than "hello", and lower() on both sides is
    /// the ASCII case folding OrdinalIgnoreCase does over the character set a slug can hold.
    ///
    /// Both the field name and the value are bound; neither reaches the SQL text.
    /// </remarks>
    public static (string Sql, object[] Parameters) FieldEqualsIgnoreCaseSql(string field, string value)
        => ($"lower({KeyLookup} #>> '{{}}') = lower(?)", [field, value]);

    /// <summary>Mean Earth radius in kilometres. The one constant the SQL and the C# share.</summary>
    private const double EarthRadiusKm = 6371.0088;

    private const double KmPerDegree = 2 * Math.PI * EarthRadiusKm / 360;

    /// <summary>
    /// The near filter as a SQL fragment plus its bound parameters, for <c>MatchesSql</c>.
    /// </summary>
    /// <remarks>
    /// Two stages inside one subquery. The bounding box compares the stored latitude and longitude
    /// against four bounds, which is cheap and throws away most rows; the haversine then decides the
    /// rest. The box is computed here rather than in SQL so the same bounds can be read in a test,
    /// and it is slightly wider than the radius on purpose: an approximate box that is too narrow
    /// would silently drop entries at the rim.
    ///
    /// Only a value whose <c>lat</c> and <c>lng</c> are both jsonb numbers reaches the casts. That is
    /// a CASE rather than an AND because Postgres does not promise to evaluate the left side of an
    /// AND first, and a cast of a stored string would fail the whole request rather than the row.
    /// A missing field, a non-object, or a malformed point is NULL, which COALESCE turns into "not
    /// within", so the row is left out instead of the query failing.
    ///
    /// Field name, bounds, centre and radius all travel as parameters. Nothing caller-supplied
    /// reaches the SQL text.
    ///
    /// Great-circle distance on a sphere. Good for "within 10 km" and wrong by up to a third of a
    /// percent against the ellipsoid, which is not the question a store locator is asking.
    /// </remarks>
    public static (string Sql, object[] Parameters) NearSql(DeliveryNear near)
    {
        var (minLat, maxLat, minLng, maxLng) = BoundingBox(near);

        // Placeholders in text order: the four bounds, then the haversine's lat, lat, lng, then the
        // radius, then the field name. HaversineSql documents its own order.
        var sql =
            "COALESCE((SELECT CASE WHEN " + GeoIsPointSql + " THEN "
            + GeoLatSql + " BETWEEN ? AND ? AND " + GeoLngSql + " BETWEEN ? AND ? AND "
            + HaversineSql("?", "?") + " <= ? END "
            + "FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) LIMIT 1), false)";

        return (sql, [minLat, maxLat, minLng, maxLng, near.Lat, near.Lat, near.Lng, near.RadiusKm, near.Field]);
    }

    /// <summary>The ORDER BY fragment for <c>sort=distance</c>, with CreatedAt as the tiebreaker.</summary>
    /// <remarks>
    /// <c>OrderBySql</c> binds nothing, so the field name and the centre are interpolated. The field
    /// name is guarded the same way <see cref="ToOrderBySql"/> guards it. The centre is two doubles
    /// that <c>Parse</c> already checked are finite and in range, printed with the invariant culture,
    /// so the text can only be digits, a sign, a point and an exponent marker.
    /// </remarks>
    public static string DistanceOrderBySql(DeliveryNear near, bool descending)
    {
        if (!IsSafeFieldName(near.Field))
            throw new InvalidOperationException(
                $"Refusing to order by distance on '{near.Field}'. A field name reaches SQL as text "
                + "and must be letters and digits only. Parse should never have produced this.");

        var direction = descending ? "DESC" : "ASC";
        var lat = near.Lat.ToString("R", CultureInfo.InvariantCulture);
        var lng = near.Lng.ToString("R", CultureInfo.InvariantCulture);

        return "(SELECT CASE WHEN " + GeoIsPointSql + " THEN " + HaversineSql(lat, lng) + " END "
             + $"FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower('{near.Field}') LIMIT 1) "
             + $"{direction} NULLS LAST, (d.data ->> 'CreatedAt')::timestamptz DESC";
    }

    /// <summary>
    /// The distance from the near filter's centre to a delivered entry's point, rounded to two
    /// decimals, or null when the entry has no readable point.
    /// </summary>
    /// <remarks>
    /// The same formula and the same radius as the SQL, so the number a caller sees is the number
    /// the rows were filtered and ordered by. Computed from the returned document rather than
    /// selected as an extra column because Marten's LINQ path cannot project a scalar alongside the
    /// document without leaving the Where chain, and leaving that chain is where the published and
    /// public predicate would get dropped.
    /// </remarks>
    public static double? DistanceKm(IReadOnlyDictionary<string, object> data, DeliveryNear near)
    {
        var key = data.Keys.FirstOrDefault(k => string.Equals(k, near.Field, StringComparison.OrdinalIgnoreCase));
        if (key is null || !barakoCMS.Core.Validation.FieldTypeRegistry.TryReadGeoPoint(data[key], out var lat, out var lng))
            return null;

        return Math.Round(Haversine(near.Lat, near.Lng, lat, lng), 2, MidpointRounding.AwayFromZero);
    }

    internal static double Haversine(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = Radians(lat2 - lat1);
        var dLng = Radians(lng2 - lng1);
        var a = Math.Pow(Math.Sin(dLat / 2), 2)
              + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Pow(Math.Sin(dLng / 2), 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>
    /// A box that contains every point within the radius, clamped to the globe.
    /// </summary>
    /// <remarks>
    /// Latitude bounds are exact on a sphere. The longitude half-width is
    /// <c>asin(sin(r/R) / cos(lat))</c>, which is the true extent rather than the flat-earth
    /// <c>r / (R cos lat)</c>, and both are widened by a tenth of a percent so rounding can never
    /// exclude a point the haversine would accept. A box that reaches a pole or crosses the
    /// antimeridian opens the longitude to the full range: still a correct prefilter, just a
    /// looser one, which beats the alternative of a wrapped box that is wrong.
    /// </remarks>
    internal static (double MinLat, double MaxLat, double MinLng, double MaxLng) BoundingBox(DeliveryNear near)
    {
        const double slack = 1.001;
        var dLat = near.RadiusKm / KmPerDegree * slack;
        var minLat = Math.Max(-90, near.Lat - dLat);
        var maxLat = Math.Min(90, near.Lat + dLat);

        var ratio = Math.Sin(near.RadiusKm / EarthRadiusKm) / Math.Cos(Radians(near.Lat));
        if (minLat <= -90 || maxLat >= 90 || ratio >= 1)
            return (minLat, maxLat, -180, 180);

        var dLng = Degrees(Math.Asin(ratio)) * slack;
        var minLng = near.Lng - dLng;
        var maxLng = near.Lng + dLng;
        if (minLng < -180 || maxLng > 180)
            return (minLat, maxLat, -180, 180);

        return (minLat, maxLat, minLng, maxLng);
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
    private static double Degrees(double radians) => radians * 180 / Math.PI;

    // Inside the jsonb_each subquery, e.value is the field's stored value.
    private const string GeoIsPointSql =
        "jsonb_typeof(e.value -> 'lat') = 'number' AND jsonb_typeof(e.value -> 'lng') = 'number'";
    private const string GeoLatSql = "(e.value ->> 'lat')::double precision";
    private const string GeoLngSql = "(e.value ->> 'lng')::double precision";

    /// <summary>
    /// The haversine in SQL, in kilometres. The centre appears as <paramref name="lat0"/> twice and
    /// then <paramref name="lng0"/> once, in that order, which is what <see cref="NearSql"/> binds
    /// against when the three are placeholders.
    /// </summary>
    private static string HaversineSql(string lat0, string lng0) =>
        $"(2 * {EarthRadiusKm.ToString("R", CultureInfo.InvariantCulture)} * asin(least(1.0, sqrt("
        + $"power(sin(radians({GeoLatSql} - {lat0}) / 2), 2) "
        + $"+ cos(radians({lat0})) * cos(radians({GeoLatSql})) "
        + $"* power(sin(radians({GeoLngSql} - {lng0}) / 2), 2)))))";

    /// <summary>The ORDER BY fragment for a validated sort.</summary>
    /// <remarks>
    /// Marten's <c>OrderBySql</c> takes a SQL string and binds nothing, so unlike the filter path
    /// the field name is interpolated rather than parameterised. That is only acceptable because the
    /// name cannot be arbitrary text: <c>Parse</c> returns the schema's own spelling, and
    /// <c>ContentTypeValidatorService</c> refuses a field name that is not an uppercase letter
    /// followed by letters and digits, with no update endpoint that could add one later.
    ///
    /// The guard below re-checks that at the point of use rather than trusting it from two files
    /// away. It is the difference between an invariant and an assumption, and this is the one place
    /// in the codebase where a field name reaches SQL as text.
    ///
    /// Sorting on the jsonb value rather than its text projection is deliberate, and it is why
    /// <c>JsonLiteral</c> stores numbers as numbers: jsonb orders numerically within its number
    /// type, so 9 sorts below 10. Ordering the text would put 10 before 9.
    ///
    /// NULLS LAST in both directions, so entries missing the field collect at the end rather than
    /// leading an ascending page with nothing in it.
    /// </remarks>
    public static string ToOrderBySql(DeliverySort sort)
    {
        if (!IsSafeFieldName(sort.Field))
            throw new InvalidOperationException(
                $"Refusing to order by '{sort.Field}'. A sort field reaches SQL as text and must be "
                + "letters and digits only. Parse should never have produced this.");

        var direction = sort.Descending ? "DESC" : "ASC";

        // CreatedAt is the tiebreaker, in the same fragment because OrderBySql returns IQueryable
        // rather than IOrderedQueryable and there is no ThenBy to chain. Cast to timestamptz rather
        // than compared as text: the stored form trims trailing zeros from the fraction, so
        // "...53.6Z" sorts after "...53.613507Z" as text and before it as a time.
        return $"(SELECT e.value FROM jsonb_each(d.data -> 'Data') e "
             + $"WHERE lower(e.key) = lower('{sort.Field}') LIMIT 1) {direction} NULLS LAST, "
             + "(d.data ->> 'CreatedAt')::timestamptz DESC";
    }

    /// <summary>Letters and digits only, starting with a letter. No quote, no semicolon, no space.</summary>
    internal static bool IsSafeFieldName(string name) =>
        !string.IsNullOrEmpty(name)
        && char.IsLetter(name[0])
        && name.All(char.IsLetterOrDigit);

    /// <summary>
    /// The value as a JSON scalar: a number bare when the field is declared numeric, quoted
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// jsonb compares by type first, so a number stored as <c>500</c> never matches the string
    /// <c>"500"</c>. Emitting a numeric-looking value as a number is what makes a price filter work
    /// at all, and it is safe because the result is a JSON literal, not SQL: it travels as a bound
    /// parameter and is cast to jsonb by Postgres.
    /// </remarks>
    private static string JsonLiteral(string value, string declaredType)
    {
        // The schema decides, not the shape of the text. Guessing from the digits alone turns
        // filter[Title][eq]=500 on a string field into the number 500, which never equals the
        // stored string "500", and the caller sees an empty result rather than an error.
        if (barakoCMS.Core.Validation.FieldTypeRegistry.IsNumericType(declaredType)
            && decimal.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return n.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (string.Equals(declaredType, "bool", StringComparison.OrdinalIgnoreCase)
            && bool.TryParse(value, out var b))
            return b ? "true" : "false";

        return System.Text.Json.JsonSerializer.Serialize(value);
    }

    /// <summary>Neutralises the LIKE wildcards so a search for "50%" means "50%" and not "50 anything".</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Names the fields that <em>are</em> filterable, so a caller who asked for a Sensitive field
    /// cannot tell it apart from one that does not exist. Both answers are "not in this list".
    /// </summary>
    /// <summary>The schema's own spelling of a field, whatever casing the caller used.</summary>
    private static string Canonical(Dictionary<string, string> allowed, string name) =>
        allowed.Keys.First(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    private static string Unfilterable(string field, Dictionary<string, string> allowed) =>
        $"Field '{field}' is not filterable. Filterable fields: {Names(allowed)}.";

    private static string Unsortable(string field, Dictionary<string, string> allowed) =>
        $"Field '{field}' is not sortable. Sortable fields: {Names(allowed)}.";

    private static string Names(Dictionary<string, string> allowed) =>
        allowed.Count == 0 ? "(none)" : string.Join(", ", allowed.Keys.OrderBy(x => x, StringComparer.Ordinal));
}
