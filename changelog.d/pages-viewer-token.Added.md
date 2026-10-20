Pages: `GET /api/public/pages/resolve` honours `X-Viewer-Token`. A valid token for the request's tenant also resolves Members pages, and the answer is private and not stored. A token that does not verify answers 401, as on the core public routes. The response now varies on `X-Viewer-Token`. `IPublicContentProjector` gains `Project` and `ProjectAsync` overloads taking `member`, and `ViewerAudience` exposes the token check to modules.

The Pages HTTP contract moves from 2 to 3: a public page requested with an invalid or other-tenant viewer token now answers 401 where it answered 200.
