- **A job's stored error could hold a full URL.** A URL in the exception message is now cut to its
  scheme and host before the error is stored, and `GET /api/jobs` applies the same cut to errors
  stored before this release.
