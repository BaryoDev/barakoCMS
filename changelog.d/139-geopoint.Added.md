- **A `geopoint` field type and a proximity filter on delivery.** A field can now hold
  `{ "lat": number, "lng": number }`, validated as a real coordinate pair rather than free text, and
  `GET /api/public/{type}?filter[Location][near]=lat,lng,radiusKm` returns the entries within the
  radius. Each item then carries `distanceKm`, and `sort=distance` orders by it. No PostGIS: the
  query is a bounding box then the haversine, both in SQL over the stored JSONB, and it sits in the
  same chain as every other filter so a Draft inside the radius stays invisible. The radius is
  capped by `Delivery:MaxRadiusKm` (default 1000) so the prefilter always applies. Distances are
  great-circle, right for "within 10 km" and not for geodesy. The console's map editor is
  barakoBrew's side.
